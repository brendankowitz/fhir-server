// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
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
using Microsoft.Health.Fhir.Core.Features;
using Microsoft.Health.SqlServer.Features.Schema;
using IgnixaSearchOptions = Ignixa.Search.Models.SearchOptions;
using IgnixaSearchParameterInfo = Ignixa.Search.Models.SearchParameterInfo;
using ResourceVersionType = Microsoft.Health.Fhir.Core.Features.Search.ResourceVersionType;
using SearchParamType = Ignixa.Specification.ValueSets.Normative.SearchParamType;

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
        private readonly global::Ignixa.Search.Definition.ICompartmentDefinitionManager _compartmentDefinitionManager;
        private readonly global::Ignixa.Search.Definition.ISearchParameterDefinitionManager _searchParameterDefinitionManager;
        private readonly ILogger<IgnixaSqlCompilerAdapter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaSqlCompilerAdapter"/> class.
        /// </summary>
        /// <param name="resolver">The Ignixa SQL symbol resolver backed by the FHIR Server SQL catalog.</param>
        /// <param name="schemaInformation">The current FHIR Server SQL schema information.</param>
        /// <param name="compartmentDefinitionManager">Ignixa's compartment definitions, used to expand a compartment search into the reference search parameters that define membership.</param>
        /// <param name="searchParameterDefinitionManager">Ignixa's search parameter definitions, used alongside the compartment definitions to resolve those reference parameters.</param>
        /// <param name="logger">The logger. Only capability metadata is ever logged, never raw search values or SQL.</param>
        public IgnixaSqlCompilerAdapter(
            ISymbolResolver resolver,
            SchemaInformation schemaInformation,
            global::Ignixa.Search.Definition.ICompartmentDefinitionManager compartmentDefinitionManager,
            global::Ignixa.Search.Definition.ISearchParameterDefinitionManager searchParameterDefinitionManager,
            ILogger<IgnixaSqlCompilerAdapter> logger)
        {
            EnsureArg.IsNotNull(resolver, nameof(resolver));
            EnsureArg.IsNotNull(schemaInformation, nameof(schemaInformation));
            EnsureArg.IsNotNull(compartmentDefinitionManager, nameof(compartmentDefinitionManager));
            EnsureArg.IsNotNull(searchParameterDefinitionManager, nameof(searchParameterDefinitionManager));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _resolver = resolver;
            _schemaInformation = schemaInformation;
            _compartmentDefinitionManager = compartmentDefinitionManager;
            _searchParameterDefinitionManager = searchParameterDefinitionManager;
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

            // The FHIR $includes sub-operation. When the request carries an IncludesContinuationToken, the
            // caller already has the match page and is asking only for the remaining included resources, paged
            // globally over the union of every include stage. Translate that token into the three inputs the
            // Ignixa emitter needs - IncludesOnly, the match surrogate window, and the resume cursor - or, for
            // the shapes still owned by legacy (count-only, unparseable token, sorted second phase), return a
            // capability failure so the request falls back to the legacy include machinery.
            IgnixaSqlCompilationOutcome includesFailure = TryBuildIncludesOnlyWindow(
                searchOptions,
                out bool includesOnly,
                out SurrogateIdRange includesSurrogateRange,
                out IncludeCursor includeCursor);
            if (includesFailure != null)
            {
                return includesFailure;
            }

            // The legacy SQL search service issues two query phases for sorts on parameters that may be
            // missing. Ascending sorts search missing values first, while descending sorts search valued
            // values first. Map the legacy phase flag to the corresponding Ignixa sort phase.
            //
            // Two legacy flags suppress the missing-values phase outright, and both must force Valued regardless
            // of direction or phase flag:
            //   * IsSortWithFilter - the sort parameter also appears as a filter, so SortRewriter emits a
            //     SortWithFilter table expression and SearchImpl never sets SortQuerySecondPhase (see the
            //     !IsSortWithFilter guard on the second-phase block). Rows without a value cannot satisfy the
            //     filter, so there is nothing for a missing phase to find.
            //   * SortHasMissingModifier - a ":missing=false" on the sort parameter. SortRewriter's
            //     "!matchFound && !sortHasMissingModifier" guard skips the whole NotExists-emitting block, so
            //     legacy again only ever runs the valued phase.
            // Deriving the phase from direction alone would emit MissingPrimary for an ascending first page in
            // both cases and silently return the complement of the correct rows.
            // A count-only request never paginates. SqlServerSearchService gates its whole continuation-token
            // block on !CountOnly, so legacy neither seeks past the boundary nor validates the token - it counts
            // the entire result set. This is reachable rather than theoretical: _total=accurate on any page after
            // the first flips CountOnly on the same options object, token and all, to compute the total. Dropping
            // the token here is therefore agreement with legacy, not a shortcut around it.
            ContinuationToken continuation = string.IsNullOrWhiteSpace(searchOptions.ContinuationToken) || searchOptions.CountOnly
                ? null
                : ContinuationToken.FromString(searchOptions.ContinuationToken);

            // SearchImpl mints a "special" token - sentinel sort value, surrogate id 0 - when the valued phase
            // filled the page exactly and a probe found more rows in the other segment. SortRewriter recognises
            // that token, discards it, and runs the second phase. SortQuerySecondPhase is a per-request field
            // that is false on the fresh request carrying that token, so the sentinel must be honoured here too
            // or the second page would repeat the first phase.
            bool sentinelSecondPhase = continuation != null
                && continuation.ResourceSurrogateId == 0
                && string.Equals(continuation.SortValue, SqlSearchConstants.SortSentinelValueForCt, StringComparison.Ordinal);

            SortPhase sortPhase = SortPhase.Valued;
            if (requestedSort.Count > 0 &&
                !ResourceColumnLoweringRule.IsResourceColumnCode(requestedSort[0].Parameter.Code) &&
                !searchOptions.IsSortWithFilter &&
                !searchOptions.SortHasMissingModifier)
            {
                bool ascendingSort = requestedSort[0].SortOrder == SortOrder.Ascending;

                // Mirrors SortRewriter's branch order exactly, which is NOT a single xor of direction and
                // phase flag once continuation tokens are in play:
                //   * second phase (flag or sentinel token) -> the complement of the first phase, so the
                //     missing segment for descending and the valued segment for ascending;
                //   * otherwise a token decides, because it was minted by the phase that produced it: a token
                //     carrying a sort value came from the valued segment, one without came from the missing
                //     segment. This holds for BOTH directions - a descending second-phase page carries no sort
                //     value and must stay in the missing segment, which a direction-based xor would send back
                //     to valued and re-return the first page's rows;
                //   * with no token at all this is the first page, whose phase is direction-driven.
                bool missingValuesPhase;
                if (searchOptions.SortQuerySecondPhase || sentinelSecondPhase)
                {
                    missingValuesPhase = !ascendingSort;
                }
                else if (continuation != null)
                {
                    missingValuesPhase = string.IsNullOrEmpty(continuation.SortValue);
                }
                else
                {
                    missingValuesPhase = ascendingSort;
                }

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
            //
            // accessConstraints (SMART v2 scopes carrying search parameters) is the third such pair: Resolve
            // must walk each constraint's predicate so its search parameters land in the symbol table, and the
            // same list is forwarded to LowerOptions.AccessConstraints. Passing it to only one of the two is
            // the failure mode this comment exists to prevent - resolve-only compiles a plan that enforces
            // nothing, lower-only cannot resolve the predicate's symbols.
            ResolvedSymbols resolved = await Resolve.RunAsync(
                ignixaOptions.Expression,
                includes,
                revIncludes,
                requestedSort,
                _resolver,
                resourceType,
                cancellationToken,
                compartmentDefinitionManager: _compartmentDefinitionManager,
                searchParameterDefinitionManager: _searchParameterDefinitionManager,
                additionalResourceTypes: ignixaOptions.ResourceTypes,
                accessConstraints: ignixaOptions.AccessConstraints,
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
            // the legacy path applies, for both the default surrogate-id order and a custom single-key _sort.
            PageSpec page = null;
            if (continuation != null && !sentinelSecondPhase)
            {
                // A custom-sort token is minted from SearchOptions.Sort - one array slot per sort key, with
                // _type and _lastUpdated mapped to the identity columns and every other key to the sort value.
                // A single-key custom sort therefore mints [sortValue, surrogateId] with no ResourceTypeId slot
                // at all, because legacy's sorted keyset compares Sid1 alone. Ignixa's PageSpec always carries a
                // type boundary, so supply the search's own resource type id when the token omits one - within a
                // single-type search "T1 = @type AND Sid1 > @sid" is exactly legacy's "Sid1 > @sid".
                short? scopedResourceTypeId = null;
                if (continuation.ResourceTypeId == null && resourceType != null && (ignixaOptions.ResourceTypes?.Count ?? 0) <= 1)
                {
                    scopedResourceTypeId = await _resolver.GetResourceTypeIdAsync(resourceType, cancellationToken);
                }

                IgnixaSqlCompilationOutcome pageFailure = TryBuildKeysetPage(continuation, requestedSort, sortPhase, scopedResourceTypeId, out page);
                if (pageFailure != null)
                {
                    return pageFailure;
                }
            }
            else if (continuation == null && !searchOptions.CountOnly && !string.IsNullOrWhiteSpace(searchOptions.ContinuationToken))
            {
                // The token was present but did not parse. Legacy tolerates this shape; Ignixa must not
                // silently drop the boundary and re-serve page one.
                return CapabilityFailure("page", "continuation-token-unparseable", EmptyUnresolvedParameters);
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

                        // SMART v2 scopes that carry search parameters. An allow-list says which types the
                        // caller may see; this says which instances of a type they may see, and the compiler
                        // applies it to the match set, every include stage and every chain target. Legacy
                        // enforces the same restriction at the match set alone, via a UNION of
                        // (_type = X AND <scope params>) legs.
                        AccessConstraints = ignixaOptions.AccessConstraints,

                        // Map the server's requested resource visibility onto the compiler's tri-state model,
                        // which filters each of the IsHistory / IsDeleted axes independently and so reproduces
                        // the legacy generator's truth table exactly - including History-only and
                        // SoftDeleted-only, which legacy renders as IsHistory = 1 / IsDeleted = 1.
                        Visibility = ToVisibility(searchOptions.ResourceVersionTypes),

                        // The FHIR $includes page. IncludesOnly drops the match arm from the output while still
                        // using it to seed the include stages; SurrogateRange bounds that seed to the exact
                        // match window the first page recorded (legacy's "Sid BETWEEN @min AND @max"); and
                        // IncludeCursor resumes the global (T1, Sid1) include stream after the last row the
                        // previous page returned. All three are null/false for an ordinary search.
                        IncludesOnly = includesOnly,
                        SurrogateRange = includesSurrogateRange,
                        IncludeCursor = includeCursor,
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
        /// Detects the FHIR <c>$includes</c> sub-operation and translates its
        /// <see cref="IncludesContinuationToken"/> into the three Ignixa emitter inputs that reproduce the
        /// legacy global include page. Returns <see langword="null"/> on success (with the out-parameters set),
        /// or a capability failure for a shape still owned by the legacy path so the caller falls back.
        /// </summary>
        /// <param name="searchOptions">The search request under compilation.</param>
        /// <param name="includesOnly">Set to <see langword="true"/> when this is an includes page.</param>
        /// <param name="surrogateRange">The inclusive match surrogate-id window, or <see langword="null"/> when not an includes page.</param>
        /// <param name="includeCursor">The include-stream resume cursor, or <see langword="null"/> on the first includes page.</param>
        /// <returns>A capability failure to fall back to legacy, or <see langword="null"/> to proceed.</returns>
        private IgnixaSqlCompilationOutcome TryBuildIncludesOnlyWindow(
            SqlSearchOptions searchOptions,
            out bool includesOnly,
            out SurrogateIdRange surrogateRange,
            out IncludeCursor includeCursor)
        {
            includesOnly = false;
            surrogateRange = null;
            includeCursor = null;

            if (string.IsNullOrEmpty(searchOptions.IncludesContinuationToken))
            {
                return null;
            }

            IncludesContinuationToken token = IncludesContinuationToken.FromString(searchOptions.IncludesContinuationToken);
            if (token == null)
            {
                // Legacy SearchIncludeImpl throws BadRequestException on an unparseable includes token. Declining
                // here hands the same request to the legacy path, which raises that error - Ignixa must not
                // silently serve an unbounded include stream in its place.
                return CapabilityFailure("includes", "includes-continuation-token-unparseable", EmptyUnresolvedParameters);
            }

            // Deferred: the sorted two-phase $includes protocol. SortQuerySecondPhase and a
            // SecondPhaseContinuationToken belong to SqlServerSearchService's second-phase-sort machinery, which
            // reconstructs the match set across an ascending/descending phase boundary before paging includes.
            // Ignixa has no equivalent for that cross-phase reconstruction, so these shapes stay on legacy. This
            // is a scoping decision, not a capability the compiler lacks outright - the plain (unsorted) include
            // page is what this change delivers.
            if (token.SortQuerySecondPhase == true || token.SecondPhaseContinuationToken != null)
            {
                return CapabilityFailure("includes", "includes-second-phase-sort", EmptyUnresolvedParameters);
            }

            // A count-only $includes is contradictory: Lower rejects IncludesOnly together with CountOnly, and
            // included resources are never counted anyway (legacy discards the includes for a count). Fall back.
            if (searchOptions.CountOnly)
            {
                return CapabilityFailure("includes", "includes-count-only", EmptyUnresolvedParameters);
            }

            includesOnly = true;

            // The match window is supplied as an Ignixa SurrogateRange rather than pushed through the expression
            // pipeline as the legacy synthetic SqlSearchParameters.ResourceSurrogateIdParameter: that parameter
            // is a legacy-only construct with no Ignixa search-parameter equivalent, whereas SurrogateRange is
            // exactly the compiler's inclusive "bound the match set by surrogate id" input.
            surrogateRange = new SurrogateIdRange(
                new SqlParameterRef(token.MatchResourceSurrogateIdMin),
                new SqlParameterRef(token.MatchResourceSurrogateIdMax));

            // No cursor on the first includes page: both include fields are null until a page has returned at
            // least one include row, and the token mints them together, so require both before resuming.
            if (token.IncludeResourceTypeId != null && token.IncludeResourceSurrogateId != null)
            {
                includeCursor = new IncludeCursor(token.IncludeResourceTypeId.Value, token.IncludeResourceSurrogateId.Value);
            }

            return null;
        }

        /// <summary>
        /// Translates the FHIR Server continuation token into the compiler's keyset <see cref="PageSpec"/>.
        /// Returns <see langword="null"/> and sets <paramref name="page"/> on success; otherwise returns a
        /// capability failure and leaves <paramref name="page"/> null so the caller falls back to legacy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two boundary shapes are produced, and which one is correct is decided by the sort phase rather than
        /// by the token alone. <see cref="SortPhase.Valued"/> makes every sort key active, so the compiler's
        /// seek predicate requires exactly one boundary value per key; <see cref="SortPhase.MissingPrimary"/>
        /// drops the primary key, so for a single-key sort it requires none and the seek degenerates to the
        /// (T1, Sid1) tiebreak - which is exactly how legacy pages through the missing segment. A boundary
        /// whose length disagrees with the active key count makes the compiler throw rather than silently
        /// mis-seek, so this method must never guess.
        /// </para>
        /// <para>
        /// The token carries exactly one <c>SortValue</c>, so at most one key may be a search-parameter sort
        /// key, and it must be the first. The three resource-column sort keys are different: their sort
        /// values are columns the token already identifies, so <c>_lastUpdated</c> reconstructs its boundary
        /// from <c>ResourceSurrogateId</c> and <c>_type</c> from <c>ResourceTypeId</c> - which is what makes
        /// <c>(_type, _lastUpdated)</c>, the only multi-key sort the SQL sorting validator admits, pageable.
        /// <c>_id</c> is the exception among them: the token captures no <c>ResourceId</c>, so it is refused.
        /// </para>
        /// <para>
        /// In the valued phase the primary key's value expression is the raw column (Emit skips the
        /// ISNULL/sentinel wrapper when the key is guaranteed non-null), so the decoded token value compares
        /// directly against it with no substitution needed. The value must be typed the way the column is:
        /// legacy round-trips a date sort value through the round-trip ("o") format, and binding that string
        /// against a datetime2 column would compare under string rather than chronological ordering.
        /// </para>
        /// </remarks>
        private IgnixaSqlCompilationOutcome TryBuildKeysetPage(
            ContinuationToken token,
            IReadOnlyList<SortExpression> requestedSort,
            SortPhase sortPhase,
            short? scopedResourceTypeId,
            out PageSpec page)
        {
            page = null;

            // The seek's ResourceTypeId boundary comes from the token for the default surrogate order, or from
            // the search's own type when it is single-type scoped. A multi-type custom-sort token has neither:
            // its slots are [sortValue, surrogateId] and there is no one type to substitute.
            //
            // Ignixa can seek without a type boundary, but only for a custom (search-parameter) sort key, whose
            // ORDER BY it emits as (sortKeys..., Sid1) with no type term - sound because ResourceSurrogateId is
            // globally unique, so that is already a total order. Every other shape orders type-major and would
            // disagree with a surrogate-only seek, dropping rows across the page seam; Ignixa rejects those, so
            // refuse them here rather than compiling a plan it will throw on.
            bool hasSearchParameterSortKey = false;
            for (int i = 0; i < requestedSort.Count; i++)
            {
                if (!ResourceColumnLoweringRule.IsResourceColumnCode(requestedSort[i].Parameter.Code))
                {
                    hasSearchParameterSortKey = true;
                    break;
                }
            }

            short? boundaryResourceTypeId = token.ResourceTypeId ?? scopedResourceTypeId;
            if (boundaryResourceTypeId == null && !hasSearchParameterSortKey)
            {
                return CapabilityFailure("page", "continuation-token-no-type", EmptyUnresolvedParameters);
            }

            SqlParameterRef[] boundary = Array.Empty<SqlParameterRef>();

            // The seek predicate needs exactly one boundary value per *active* sort key, in the same order
            // Emit renders them. The missing-primary phase drops the primary key (every row in that segment
            // has no value for it), so its active keys start at index 1; the valued phase uses all of them.
            int firstActiveKey = sortPhase == SortPhase.MissingPrimary ? 1 : 0;
            var boundaryValues = new List<SqlParameterRef>();

            for (int i = firstActiveKey; i < requestedSort.Count; i++)
            {
                string code = requestedSort[i].Parameter.Code;

                // The two resource-column sort keys need no value from the token beyond the identity pair it
                // already carries, because their sort values *are* that pair: _lastUpdated orders by
                // ResourceSurrogateId and _type by ResourceTypeId. That is precisely why (_type, _lastUpdated) -
                // the only multi-key sort the SQL sorting validator admits - is keyset-pageable at all.
                if (code == KnownQueryParameterNames.LastUpdated)
                {
                    boundaryValues.Add(new SqlParameterRef(token.ResourceSurrogateId));
                    continue;
                }

                if (code == KnownQueryParameterNames.Type)
                {
                    // Unreachable without a type boundary: a _type sort key makes this a resource-column sort,
                    // and the guard above already refused a typeless boundary for those.
                    boundaryValues.Add(new SqlParameterRef(boundaryResourceTypeId.Value));
                    continue;
                }

                // _id is the third sortable resource column, but unlike the other two the continuation token
                // captures no ResourceId, so there is no boundary value to reconstruct.
                if (code == KnownQueryParameterNames.Id)
                {
                    return CapabilityFailure("page", "continuation-token-id-sort", EmptyUnresolvedParameters);
                }

                if (i > 0)
                {
                    // A search-parameter sort key's boundary comes from the token's single SortValue slot, so
                    // only the first key can have one; a second such key has no reconstructable seek predicate.
                    return CapabilityFailure("page", "continuation-token-multi-key-sort", EmptyUnresolvedParameters);
                }

                if (string.IsNullOrEmpty(token.SortValue))
                {
                    // The valued phase needs a boundary value for its one active key. A token minted by the
                    // missing segment carries none, so honouring it here would seek from an empty boundary
                    // and re-serve the valued segment from the top.
                    return CapabilityFailure("page", "continuation-token-missing-sort-value", EmptyUnresolvedParameters);
                }

                if (!TryTypeSortValue(requestedSort[i], token.SortValue, out object typedSortValue))
                {
                    return CapabilityFailure("page", "continuation-token-sort-value-type", EmptyUnresolvedParameters);
                }

                boundaryValues.Add(new SqlParameterRef(typedSortValue));
            }

            if (!hasSearchParameterSortKey && !string.IsNullOrEmpty(token.SortValue))
            {
                // No search-parameter sort key, yet the token carries a sort value: it was minted for a
                // different query shape than the one being compiled, and a keyset built only from the
                // identity pair cannot honour it.
                return CapabilityFailure("page", "continuation-token-sort-value", EmptyUnresolvedParameters);
            }

            boundary = boundaryValues.ToArray();
            page = new PageSpec(
                boundary,
                boundaryResourceTypeId == null ? null : new SqlParameterRef(boundaryResourceTypeId.Value),
                new SqlParameterRef(token.ResourceSurrogateId));

            return null;
        }

        /// <summary>
        /// Converts the token's string sort value into the CLR type the sort column binds as, mirroring the
        /// legacy generator's <c>GetSortRelatedDetails</c>: date parameters round-trip through the "o" format,
        /// string parameters bind as-is. Any other sort parameter type is refused rather than guessed at.
        /// </summary>
        private static bool TryTypeSortValue(SortExpression sort, string rawSortValue, out object typedSortValue)
        {
            switch (sort.Parameter.Type)
            {
                case SearchParamType.Date:
                    if (DateTime.TryParseExact(rawSortValue, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                    {
                        typedSortValue = parsed;
                        return true;
                    }

                    typedSortValue = null;
                    return false;

                case SearchParamType.String:
                    typedSortValue = rawSortValue;
                    return true;

                default:
                    typedSortValue = null;
                    return false;
            }
        }

        /// <summary>
        /// Maps the FHIR Server's <see cref="ResourceVersionType"/> onto the SQL compiler's tri-state
        /// <see cref="ResourceVisibility"/>, where <see langword="null"/> on an axis means "emit no filter",
        /// <see langword="false"/> means <c>= 0</c> and <see langword="true"/> means <c>= 1</c>.
        /// </summary>
        /// <remarks>
        /// This reproduces the legacy generator's <c>AppendHistoryClause</c> / <c>AppendDeletedClause</c> truth
        /// table exactly:
        /// <list type="table">
        /// <item><description>Latest                  -> IsHistory = 0, IsDeleted = 0</description></item>
        /// <item><description>History                 -> IsHistory = 1, no deleted filter</description></item>
        /// <item><description>SoftDeleted             -> no history filter, IsDeleted = 1</description></item>
        /// <item><description>Latest | History        -> no history filter, IsDeleted = 0</description></item>
        /// <item><description>Latest | SoftDeleted    -> IsHistory = 0, no deleted filter</description></item>
        /// <item><description>History | SoftDeleted   -> IsHistory = 1, IsDeleted = 1</description></item>
        /// <item><description>all three               -> no filter on either axis</description></item>
        /// </list>
        /// Each axis is anchored on <see cref="ResourceVersionType.Latest"/>, which asks for live current rows
        /// (IsHistory = 0 AND IsDeleted = 0); History and SoftDeleted each opt one axis away from that anchor.
        /// That is what makes the "no filter" corners (Latest|History on the history axis, Latest|SoftDeleted on
        /// the deleted axis, and History alone on the deleted axis) fall out rather than needing special cases.
        /// </remarks>
        private static ResourceVisibility ToVisibility(ResourceVersionType versionTypes)
        {
            bool wantsLatest = versionTypes.HasFlag(ResourceVersionType.Latest);
            bool wantsHistory = versionTypes.HasFlag(ResourceVersionType.History);
            bool wantsSoftDeleted = versionTypes.HasFlag(ResourceVersionType.SoftDeleted);

            // Both axes are anchored on Latest, which is the flag that asks for live current rows: IsHistory = 0
            // AND IsDeleted = 0. History and SoftDeleted each opt one axis away from that anchor. Asking for both
            // sides of an axis (or neither, because the other flag drives that axis) means no filter can be
            // correct, so none is emitted.
            //
            // Note this must never return null for the "unfiltered on both axes" case: a null Visibility means
            // "use the compiler default", which is Current - the opposite of unfiltered.
            return new ResourceVisibility(
                AxisFilter(wantsCurrent: wantsLatest, wantsNonCurrent: wantsHistory),
                AxisFilter(wantsCurrent: wantsLatest, wantsNonCurrent: wantsSoftDeleted));

            static bool? AxisFilter(bool wantsCurrent, bool wantsNonCurrent)
            {
                return wantsCurrent == wantsNonCurrent ? null : wantsNonCurrent;
            }
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
