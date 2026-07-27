// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Ignixa.Search.Expressions;
using Ignixa.Search.Sql.Ast;
using Ignixa.Search.Sql.Builders;
using Ignixa.Search.Sql.Lowering;
using Ignixa.Search.Sql.Symbols;
using Microsoft.Extensions.Logging;
using Microsoft.Health.SqlServer.Features.Schema;
using IgnixaSearchOptions = Ignixa.Search.Models.SearchOptions;
using IgnixaSearchParameterInfo = Ignixa.Search.Models.SearchParameterInfo;
using ResourceVersionType = Microsoft.Health.Fhir.Core.Features.Search.ResourceVersionType;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa
{
    /// <summary>
    /// Compile-only adapter that invokes the coordinated Ignixa SQL compiler stages
    /// (<see cref="Resolve"/>, <see cref="Lower"/>, <see cref="SqlBuilder"/>) against a <see cref="SqlSearchOptions"/>
    /// request and returns an in-memory compilation artifact.
    /// </summary>
    /// <remarks>
    /// This adapter never executes SQL, opens a connection, binds parameters, hydrates resources, or
    /// replaces the legacy FHIR Server SQL response path. It is intentionally a narrow, compile-only
    /// boundary; capability routing is added separately.
    /// </remarks>
    internal sealed class IgnixaSqlCompilerAdapter : IIgnixaSqlCompilerAdapter
    {
        private const string UnknownVersion = "unknown";

        /// <summary>
        /// The Ignixa package versions and commit this assembly compiled against, discovered at runtime
        /// rather than hardcoded. The versions come from assembly metadata stamped by the SqlServer project
        /// from the resolved <c>Directory.Packages.props</c> values; the commit comes from the Ignixa
        /// assembly's own informational version (<c>1.0.0+&lt;sha&gt;</c>). Hardcoded constants silently drifted
        /// from the packages actually referenced, so this metadata reported a version and a commit that were
        /// never the ones the emitted SQL came from.
        /// </summary>
        private static readonly string SearchPackageVersionValue = ReadStampedVersion("IgnixaSearchPackageVersion");
        private static readonly string SearchSqlPackageVersionValue = ReadStampedVersion("IgnixaSearchSqlPackageVersion");
        private static readonly string IgnixaCommitValue = ReadIgnixaCommit();

        private static readonly IReadOnlyList<IncludeExpression> EmptyIncludes = Array.Empty<IncludeExpression>();
        private static readonly IReadOnlyList<SortExpression> EmptySort = Array.Empty<SortExpression>();
        private static readonly IReadOnlyList<IgnixaSearchParameterInfo> EmptyUnresolvedParameters = Array.Empty<IgnixaSearchParameterInfo>();

        private readonly ISymbolResolver _resolver;
        private readonly SchemaInformation _schemaInformation;
        private readonly ILogger<IgnixaSqlCompilerAdapter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaSqlCompilerAdapter"/> class.
        /// </summary>
        /// <param name="resolver">The Ignixa SQL symbol resolver backed by the FHIR Server SQL catalog.</param>
        /// <param name="schemaInformation">The current FHIR Server SQL schema information.</param>
        /// <param name="logger">The logger. Only capability metadata is ever logged, never raw search values or SQL.</param>
        public IgnixaSqlCompilerAdapter(
            ISymbolResolver resolver,
            SchemaInformation schemaInformation,
            ILogger<IgnixaSqlCompilerAdapter> logger)
        {
            EnsureArg.IsNotNull(resolver, nameof(resolver));
            EnsureArg.IsNotNull(schemaInformation, nameof(schemaInformation));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _resolver = resolver;
            _schemaInformation = schemaInformation;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IgnixaSqlCompilationOutcome> CompileAsync(
            SqlSearchOptions searchOptions,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(searchOptions, nameof(searchOptions));
            EnsureArg.IsNotNull(searchOptions.IgnixaOptions, nameof(searchOptions.IgnixaOptions));

            IgnixaSearchOptions ignixaOptions = searchOptions.IgnixaOptions;

            // A null/empty resource type means a multi-type or system-level search (GET / or GET /?_type=...),
            // a supported case rather than a caller error. Normalize it to null ONCE here so Resolve.RunAsync,
            // Lower.Run, and the SystemLevelSearch flag all observe the exact same value; an empty string would
            // read as system-level to one stage and as a literal (unmatchable) resource type to the others.
            string resourceType = string.IsNullOrEmpty(ignixaOptions.ResourceType) ? null : ignixaOptions.ResourceType;

            IReadOnlyList<SortExpression> requestedSort = ignixaOptions.Sort ?? EmptySort;

            // Count-only requests never carry includes/revincludes into either Resolve or Lower.
            IReadOnlyList<IncludeExpression> includes = searchOptions.CountOnly ? EmptyIncludes : (ignixaOptions.Include ?? EmptyIncludes);
            IReadOnlyList<IncludeExpression> revIncludes = searchOptions.CountOnly ? EmptyIncludes : (ignixaOptions.RevInclude ?? EmptyIncludes);

            // The legacy SQL search service issues two query phases for sorts on parameters that may be
            // missing. Ascending sorts search missing values first, while descending sorts search valued
            // values first. Map the legacy phase flag to the corresponding Ignixa sort phase.
            SortPhase sortPhase = SortPhase.Valued;
            if (requestedSort.Count > 0 &&
                !ResourceColumnLoweringRule.IsResourceColumnCode(requestedSort[0].Parameter.Code))
            {
                bool ascendingSort = requestedSort[0].SortOrder == SortOrder.Ascending;
                bool missingValuesPhase = ascendingSort != searchOptions.SortQuerySecondPhase;
                sortPhase = missingValuesPhase ? SortPhase.MissingPrimary : SortPhase.Valued;
            }

            // Stage 1: Resolve. This is the only stage that performs I/O (symbol lookups). A multi-_type caller
            // resolves the type names before compiling and passes them via additionalResourceTypes rather than in
            // the expression tree; the same list is forwarded to LowerOptions.ResourceTypes below and both halves
            // are required, or a multi-_type search silently widens to every type. An unresolvable type name is
            // kept by the compiler as an unmatchable sentinel, so a fully-unresolvable list cannot collapse into
            // "every type".
            //
            // allowedResourceTypes (the SMART clinical-scope allow-list) has the same two-halves requirement:
            // Lower's allow-list enforcement needs each permitted type's id, so the names must resolve here, and
            // the same list is forwarded to LowerOptions.AllowedResourceTypes below so it is actually enforced.
            // An unresolvable name is likewise kept as an unmatchable sentinel, so a typo narrows rather than
            // widens the allow-list.
            ResolvedSymbols resolved = await Resolve.RunAsync(
                ignixaOptions.Expression,
                includes,
                revIncludes,
                requestedSort,
                _resolver,
                resourceType,
                cancellationToken,
                compartmentDefinitionManager: null,
                searchParameterDefinitionManager: null,
                additionalResourceTypes: ignixaOptions.ResourceTypes,
                allowedResourceTypes: ignixaOptions.AllowedResourceTypes);

            if (resolved.Unresolved.Count > 0)
            {
                // Known capability gap: some search parameter or resource type could not be resolved.
                // Do not lower or emit; the caller falls back to the legacy SQL path.
                return CapabilityFailure("resolve", "unresolved-symbol", resolved.Unresolved);
            }

            // Keyset pagination. A continuation token that reaches Ignixa was ANDed into the legacy
            // expression tree only, never into ignixaOptions.Expression (which SearchOptionsFactory built
            // before that AND-in), so without an explicit PageSpec the Ignixa plan would re-return page one.
            // The token is translated into a PageSpec here so the compiler emits the same forward keyset seek
            // the legacy path applies. Only the default surrogate-id keyset (no custom _sort, composite
            // (ResourceTypeId, ResourceSurrogateId) boundary, no carried sort value) is wired; anything else
            // yields a capability failure and stays on the legacy path.
            PageSpec page = null;
            if (!string.IsNullOrWhiteSpace(searchOptions.ContinuationToken))
            {
                IgnixaSqlCompilationOutcome pageFailure = TryBuildSurrogateKeysetPage(searchOptions, requestedSort, out page);
                if (pageFailure != null)
                {
                    return pageFailure;
                }
            }

            LoweredPlan lowered;
            try
            {
                // Stage 2: Lower. Pure, synchronous construction of the query plan.
                lowered = Lower.Run(
                    ignixaOptions.Expression,
                    resolved.Symbols,
                    resourceType,
                    includes,
                    revIncludes,
                    ignixaOptions.IncludesMaxItemCount ?? searchOptions.IncludeCount,
                    requestedSort,
                    sortPhase,
                    page: page,
                    new LowerOptions
                    {
                        CountOnly = searchOptions.CountOnly,
                        Top = searchOptions.MaxItemCount,
                        SystemLevelSearch = resourceType is null,

                        // Without this forwarding a multi-_type search silently returns EVERY resource type
                        // rather than the requested subset: the cross-type leaves carry no ResourceTypeId of
                        // their own, so nothing else narrows them.
                        ResourceTypes = ignixaOptions.ResourceTypes,

                        // SMART clinical scopes. Without this the compiler accepts the allow-list and enforces
                        // nothing: the match set would be ungated and, worse, an _include would return resource
                        // types the scope never granted, because Ignixa runs includes as separate row-producing
                        // stages rather than as a filter over the match set.
                        AllowedResourceTypes = ignixaOptions.AllowedResourceTypes,

                        // Map the server's requested resource visibility onto the compiler's relaxation-only
                        // model. The router only routes Latest-inclusive combinations here, for which this
                        // mapping is exact; History-only / SoftDeleted-only (which legacy renders as IsHistory=1
                        // / IsDeleted=1, a filter ResourceVisibility cannot express) stay on the legacy path.
                        Visibility = ToVisibility(searchOptions.ResourceVersionTypes),
                    });
            }
            catch (NotSupportedException ex)
            {
                // The developmental package surfaces capability gaps in lowering as this narrow exception
                // type. This is the only catch boundary in the adapter; cancellation, resolver/model
                // exceptions, and SqlBuilder.Run exceptions are never caught here.
                _logger.LogInformation(
                    "Ignixa lowering reported a capability gap of type {ExceptionType}.",
                    ex.GetType().Name);
                return CapabilityFailure("lower", "not-supported", EmptyUnresolvedParameters, ex.GetType().Name);
            }

            IgnixaSqlCompilationOutcome shapeFailure = ValidateResultShape(searchOptions, ignixaOptions, lowered);
            if (shapeFailure != null)
            {
                return shapeFailure;
            }

            // Stage 3: Emit. Pure, synchronous rendering of parameterized SQL. No catch boundary: any
            // exception here is unexpected and must propagate. Count-only plans ignore projection and emit a
            // single scalar; every other plan projects the dbo.Resource columns the execution reader needs so
            // the emitted SQL can be materialised (not merely observed).
            QueryPlan planToEmit = lowered.Plan.CountOnly
                ? lowered.Plan
                : lowered.Plan with { Projection = new ProjectionSpec(IgnixaResourceReader.ProjectionColumns) };

            EmittedSql emitted = SqlBuilder.Run(planToEmit);

            return new IgnixaSqlCompilationOutcome(
                Compiled: true,
                FailureStage: null,
                FailureKind: null,
                FailureMessage: null,
                LoweredPlan: lowered,
                EmittedSql: emitted,
                UnresolvedParameters: resolved.Unresolved,
                SearchPackageVersion: SearchPackageVersionValue,
                SearchSqlPackageVersion: SearchSqlPackageVersionValue,
                IgnixaCommit: IgnixaCommitValue,
                SchemaVersion: _schemaInformation.Current,
                PlanFingerprint: CreatePlanFingerprint(lowered.Plan));
        }

        /// <summary>
        /// Validates that the lowered plan's result shape matches what was requested, without parsing SQL
        /// text. Returns <see langword="null"/> when the shape matches, or a capability outcome describing
        /// the specific mismatch.
        /// </summary>
        /// <remarks>
        /// Internal (rather than private) solely so that unit tests can exercise each classification branch
        /// directly against a deliberately mismatched, hand-built <see cref="LoweredPlan"/> without depending
        /// on an undocumented divergence in the real Ignixa lowering behavior.
        /// </remarks>
        internal IgnixaSqlCompilationOutcome ValidateResultShape(
            SqlSearchOptions searchOptions,
            IgnixaSearchOptions ignixaOptions,
            LoweredPlan lowered)
        {
            QueryPlan plan = lowered.Plan;

            if (searchOptions.CountOnly != plan.CountOnly)
            {
                return CapabilityFailure("shape", "count-only-mismatch", EmptyUnresolvedParameters);
            }

            int requestedIncludeCount = searchOptions.CountOnly
                ? 0
                : (ignixaOptions.Include?.Count ?? 0) + (ignixaOptions.RevInclude?.Count ?? 0);
            int emittedIncludeCount = plan.Includes?.Count ?? 0;
            if (requestedIncludeCount != emittedIncludeCount)
            {
                return CapabilityFailure("shape", "include-shape-mismatch", EmptyUnresolvedParameters);
            }

            if ((ignixaOptions.Sort?.Count ?? 0) > 0 && plan.Sort is null)
            {
                return CapabilityFailure("shape", "sort-shape-mismatch", EmptyUnresolvedParameters);
            }

            if (searchOptions.MaxItemCount != plan.Top)
            {
                return CapabilityFailure("shape", "top-shape-mismatch", EmptyUnresolvedParameters);
            }

            return null;
        }

        /// <summary>
        /// Builds a capability failure outcome. Capability outcomes never contain raw exception text, raw
        /// search values, or a plan fingerprint; the fingerprint is only computed for successful compilations.
        /// </summary>
        private IgnixaSqlCompilationOutcome CapabilityFailure(
            string stage,
            string kind,
            IReadOnlyList<IgnixaSearchParameterInfo> unresolvedParameters,
            string failureMessage = null)
        {
            return new IgnixaSqlCompilationOutcome(
                Compiled: false,
                FailureStage: stage,
                FailureKind: kind,
                FailureMessage: failureMessage,
                LoweredPlan: null,
                EmittedSql: null,
                UnresolvedParameters: unresolvedParameters,
                SearchPackageVersion: SearchPackageVersionValue,
                SearchSqlPackageVersion: SearchSqlPackageVersionValue,
                IgnixaCommit: IgnixaCommitValue,
                SchemaVersion: _schemaInformation.Current,
                PlanFingerprint: string.Empty);
        }

        /// <summary>
        /// Reads a package version stamped into this assembly by the SqlServer project from the resolved
        /// <c>Directory.Packages.props</c> value. Returns <c>"unknown"</c> rather than throwing when the
        /// stamp is absent: this metadata is diagnostic, so a missing stamp must never fail a search.
        /// </summary>
        private static string ReadStampedVersion(string key)
        {
            foreach (AssemblyMetadataAttribute attribute in typeof(IgnixaSqlCompilerAdapter).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attribute.Key, key, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(attribute.Value))
                {
                    return attribute.Value;
                }
            }

            return UnknownVersion;
        }

        /// <summary>
        /// Reads the Ignixa source commit from the compiler assembly's informational version, which SourceLink
        /// renders as <c>&lt;version&gt;+&lt;sha&gt;</c>. Taking it from the assembly that actually emitted the SQL
        /// means the recorded commit cannot disagree with the binary in the process, which is the whole point
        /// of recording it.
        /// </summary>
        private static string ReadIgnixaCommit()
        {
            string informationalVersion = typeof(SqlBuilder).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(informationalVersion))
            {
                return UnknownVersion;
            }

            int separator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return separator >= 0 && separator < informationalVersion.Length - 1
                ? informationalVersion[(separator + 1)..]
                : UnknownVersion;
        }

        /// <summary>
        /// Translates the FHIR Server continuation token into the compiler's keyset <see cref="PageSpec"/>,
        /// but only for the default surrogate-id order this pass wires: no custom <c>_sort</c>, and a token
        /// carrying a composite (ResourceTypeId, ResourceSurrogateId) boundary with no captured sort value.
        /// Returns <see langword="null"/> and sets <paramref name="page"/> on success; otherwise returns a
        /// capability failure and leaves <paramref name="page"/> null so the caller falls back to legacy.
        /// </summary>
        /// <remarks>
        /// The boundary carries no per-key sort values (empty <see cref="PageSpec.Boundary"/>), so the
        /// emitter's ISNULL/sentinel substitution never applies here; it is required only for custom-sort
        /// keys, which this method deliberately refuses. The (ResourceTypeId, ResourceSurrogateId) pair renders
        /// as bound parameters and drives the forward tuple seek <c>(m.T1, m.Sid1) &gt; (@type, @sid)</c>, the
        /// exact composite the legacy path applies as a GreaterThan on the partitioned primary key.
        /// </remarks>
        private IgnixaSqlCompilationOutcome TryBuildSurrogateKeysetPage(
            SqlSearchOptions searchOptions,
            IReadOnlyList<SortExpression> requestedSort,
            out PageSpec page)
        {
            page = null;

            // A user _sort — including _lastUpdated or _type — drives a keyed boundary whose per-key value
            // would need Emit's ISNULL/sentinel substitution to compare equal to a live column. This pass does
            // not reproduce that, so any custom sort stays on the legacy path.
            if (requestedSort.Count > 0)
            {
                return CapabilityFailure("page", "continuation-token-custom-sort", EmptyUnresolvedParameters);
            }

            ContinuationToken token = ContinuationToken.FromString(searchOptions.ContinuationToken);
            if (token == null)
            {
                return CapabilityFailure("page", "continuation-token-unparseable", EmptyUnresolvedParameters);
            }

            // A carried sort value means the token was minted for a custom sort; the surrogate-only keyset
            // cannot honour it, and the router should not have routed it here.
            if (!string.IsNullOrEmpty(token.SortValue))
            {
                return CapabilityFailure("page", "continuation-token-sort-value", EmptyUnresolvedParameters);
            }

            // The composite seek needs a ResourceTypeId boundary. A token without one (legacy tokens, or a
            // pre-PartitionedTables schema) is handled by the legacy path as a bare ResourceSurrogateId
            // comparison, which this composite PageSpec does not reproduce.
            if (token.ResourceTypeId == null)
            {
                return CapabilityFailure("page", "continuation-token-no-type", EmptyUnresolvedParameters);
            }

            page = new PageSpec(
                Array.Empty<SqlParameterRef>(),
                new SqlParameterRef(token.ResourceTypeId.Value),
                new SqlParameterRef(token.ResourceSurrogateId));

            return null;
        }

        /// <summary>
        /// Maps the FHIR Server's <see cref="ResourceVersionType"/> onto the SQL compiler's relaxation-only
        /// <see cref="ResourceVisibility"/>. <see cref="ResourceVersionType.Latest"/> alone returns
        /// <see langword="null"/> rather than an explicit <see cref="ResourceVisibility.Current"/> — both leave
        /// the plan's effective visibility at Current, so null is the smaller diff. This mapping is faithful to
        /// the legacy generator only for Latest-inclusive combinations; the router keeps History-only and
        /// SoftDeleted-only requests (which legacy renders as an exact IsHistory=1 / IsDeleted=1 filter that a
        /// relaxation-only model cannot express) on the legacy path.
        /// </summary>
        private static ResourceVisibility ToVisibility(ResourceVersionType versionTypes)
        {
            if (versionTypes == ResourceVersionType.Latest)
            {
                return null;
            }

            return new ResourceVisibility(
                IncludeHistory: versionTypes.HasFlag(ResourceVersionType.History),
                IncludeDeleted: versionTypes.HasFlag(ResourceVersionType.SoftDeleted));
        }

        /// <summary>
        /// Creates a deterministic, redacted SHA-256 fingerprint from the plan shape only
        /// (<see cref="QueryPlan.Explain"/>). Never hashes raw search values, emitted SQL text, emitted
        /// parameter values, <see cref="Core.Features.Search.SearchOptions"/>, or exception messages.
        /// </summary>
        private static string CreatePlanFingerprint(QueryPlan plan)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(plan.Explain());
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
    }
}
