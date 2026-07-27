// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Ignixa.Search.Sql.Ast;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.SqlServer.Registration;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa
{
    /// <summary>
    /// Observes eligible search requests by compiling the Ignixa SQL plan and logging the outcome.
    /// </summary>
    /// <remarks>
    /// This router never executes the emitted SQL, opens a connection, binds parameters, hydrates
    /// resources, or replaces the legacy FHIR Server SQL response path. It is a pure compile-only
    /// observation path gated on <see cref="FhirSqlServerConfiguration.EnableIgnixaSqlCompileOnly"/>.
    /// </remarks>
    internal sealed class IgnixaSqlCompileOnlyRouter : IIgnixaSqlCompileOnlyRouter
    {
        private readonly IIgnixaSqlCompilerAdapter _adapter;
        private readonly FhirSqlServerConfiguration _configuration;
        private readonly ILogger<IgnixaSqlCompileOnlyRouter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaSqlCompileOnlyRouter"/> class.
        /// </summary>
        /// <param name="adapter">The compile-only Ignixa SQL compiler adapter.</param>
        /// <param name="configuration">The FHIR SQL Server configuration.</param>
        /// <param name="logger">The logger. Only redacted metadata is ever logged, never raw search values or SQL.</param>
        public IgnixaSqlCompileOnlyRouter(
            IIgnixaSqlCompilerAdapter adapter,
            FhirSqlServerConfiguration configuration,
            ILogger<IgnixaSqlCompileOnlyRouter> logger)
        {
            EnsureArg.IsNotNull(adapter, nameof(adapter));
            EnsureArg.IsNotNull(configuration, nameof(configuration));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _adapter = adapter;
            _configuration = configuration;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task ObserveAsync(
            SqlSearchOptions searchOptions,
            bool accessControlPredicateRequired,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(searchOptions, nameof(searchOptions));

            if (!_configuration.EnableIgnixaSqlCompileOnly)
            {
                return;
            }

            if (!IsEligible(searchOptions, accessControlPredicateRequired))
            {
                return;
            }

            // All skip conditions passed — compile exactly once.
            IgnixaSqlCompilationOutcome outcome = await _adapter.CompileAsync(searchOptions, cancellationToken);

            if (outcome.Compiled)
            {
                if (outcome.LoweredPlan == null)
                {
                    throw new InvalidOperationException(
                        "Ignixa compilation reported success but LoweredPlan was null. This is a compiler invariant violation.");
                }

                _logger.LogInformation(
                    "Ignixa compile-only observation succeeded. " +
                    "Fingerprint={Fingerprint}, CteCount={CteCount}, IncludeCount={IncludeCount}, " +
                    "HasSort={HasSort}, CountOnly={CountOnly}, SchemaVersion={SchemaVersion}",
                    outcome.PlanFingerprint,
                    outcome.LoweredPlan.Plan.Ctes?.Count ?? 0,
                    outcome.LoweredPlan.Plan.Includes?.Count ?? 0,
                    outcome.LoweredPlan.Plan.Sort != null,
                    outcome.LoweredPlan.Plan.CountOnly,
                    outcome.SchemaVersion);
            }
            else
            {
                _logger.LogInformation(
                    "Ignixa compile-only observation reported a capability gap. " +
                    "Stage={Stage}, Kind={Kind}, UnresolvedCount={UnresolvedCount}, Fingerprint={Fingerprint}",
                    outcome.FailureStage,
                    outcome.FailureKind,
                    outcome.UnresolvedParameters?.Count ?? 0,
                    outcome.PlanFingerprint);
            }
        }

        /// <inheritdoc />
        public async Task<IgnixaSqlExecutionPlan> TryCreateExecutionPlanAsync(
            SqlSearchOptions searchOptions,
            bool accessControlPredicateRequired,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(searchOptions, nameof(searchOptions));

            if (!_configuration.EnableIgnixaSqlExecution)
            {
                return null;
            }

            if (!IsEligible(searchOptions, accessControlPredicateRequired))
            {
                return null;
            }

            // The legacy generator emits TOP (MaxItemCount + 1) so the reader can detect that another page
            // exists and mint a continuation token. Ignixa emits TOP equal to the requested count exactly, so
            // for row-returning searches compile against a count bumped by one; count-only plans ignore TOP.
            SqlSearchOptions compileOptions = searchOptions;
            if (!searchOptions.CountOnly)
            {
                compileOptions = searchOptions.CloneSqlSearchOptions();
                compileOptions.MaxItemCount = searchOptions.MaxItemCount + 1;
            }

            IgnixaSqlCompilationOutcome outcome = await _adapter.CompileAsync(compileOptions, cancellationToken);

            if (!outcome.Compiled)
            {
                _logger.LogDebug(
                    "Falling back to legacy SQL: Ignixa reported a capability gap. Stage={Stage}, Kind={Kind}",
                    outcome.FailureStage,
                    outcome.FailureKind);
                return null;
            }

            if (outcome.LoweredPlan == null || outcome.EmittedSql == null)
            {
                throw new InvalidOperationException(
                    "Ignixa compilation reported success but LoweredPlan or EmittedSql was null. This is a compiler invariant violation.");
            }

            QueryPlan plan = outcome.LoweredPlan.Plan;

            // Materialisation guard for includes, now narrowed to two sub-cases.
            //
            // Plain _include/_revinclude stages materialise correctly through the shared SearchResult
            // assembly and are allowed onto the Ignixa path: the emitted UNION ALL projects
            // (T1, Sid1, IsMatch, IsPartial, [SortValueN], projection); the outer ORDER BY IsMatch DESC reads
            // every match row before any included row, so SearchImpl mints the continuation token only from the
            // last match row and never from an included row; and each include stage sets IsPartial via
            // COUNT_BIG(*) OVER() > Limit, which SearchImpl surfaces as the same partial-result signal legacy
            // raises. IgnixaResourceReader reads IsMatch/IsPartial at ordinals 2/3 when HasIncludes is set.
            //
            // Two sub-cases remain guarded because their emitted semantics have not been validated against the
            // legacy generator in this pass:
            //   * Wildcard includes (ReferenceSearchParamId == null, e.g. "_include=*") lower to a
            //     reference-parameter-less join whose row set has not been compared to legacy.
            //   * :iterate includes (Iterate == true) resolve through a single topological pass in Lower rather
            //     than legacy's fixed-point iteration, so the produced closure can diverge.
            // A plan that carries any such stage falls back to legacy.
            bool hasIncludes = (plan.Includes?.Count ?? 0) > 0;
            if (hasIncludes)
            {
                foreach (IncludeStage stage in plan.Includes)
                {
                    if (stage.Iterate)
                    {
                        _logger.LogDebug("Falling back to legacy SQL: Ignixa plan carries an iterate include. Reason={Reason}", "include-iterate");
                        return null;
                    }

                    if (stage.ReferenceSearchParamId == null)
                    {
                        _logger.LogDebug("Falling back to legacy SQL: Ignixa plan carries a wildcard include. Reason={Reason}", "include-wildcard");
                        return null;
                    }
                }
            }

            // Materialisation guard for the sort phases legacy handles specially. When the sort parameter is
            // also a filter (IsSortWithFilter) legacy skips the missing-values phase and searches valued rows
            // directly, and a ":missing" modifier on the sort parameter (SortHasMissingModifier) drives yet
            // another phase shape. The Ignixa adapter derives its Valued/MissingPrimary phase purely from
            // sort order and SortQuerySecondPhase, so it would emit the wrong phase for these two cases and
            // silently return the wrong rows. Both flags are set by the SortRewriter, which runs before this
            // router, so fall back to legacy when either is present.
            if (plan.Sort != null && (searchOptions.IsSortWithFilter || searchOptions.SortHasMissingModifier))
            {
                _logger.LogDebug(
                    "Falling back to legacy SQL: Ignixa does not model this sort phase. Reason={Reason}",
                    searchOptions.IsSortWithFilter ? "sort-with-filter" : "sort-missing-modifier");
                return null;
            }

            // A custom sort projects SortValueN keyset columns between the identity prefix and the resource
            // projection. Model exactly how many the reader must skip, and whether the primary key's value must
            // be captured for the continuation token.
            int sortKeyColumnCount = 0;
            bool captureSortValue = false;
            if (plan.Sort is { } sortSpec)
            {
                // Ignixa projects one SortValueN column per active key: every key in the Valued phase, or every
                // key except the missing primary in the MissingPrimary phase. This mirrors
                // SqlBuilder.ActiveKeyIndices, which is what the emitter uses to render the columns.
                sortKeyColumnCount = sortSpec.Phase == SortPhase.Valued
                    ? sortSpec.Keys.Count
                    : sortSpec.Keys.Count - 1;

                // The legacy reader captures a sort value into the continuation token only when the query
                // searched the valued segment of a search-parameter-table primary key (IsSortValueNeeded).
                // _lastUpdated orders by the surrogate id, which the token already carries, so it needs no
                // captured value; the MissingPrimary phase has no primary value to capture.
                captureSortValue = sortSpec.Phase == SortPhase.Valued
                    && sortSpec.Keys[0].Kind != SortKeyKind.LastUpdated;
            }

            return new IgnixaSqlExecutionPlan(outcome.EmittedSql, hasIncludes, plan.CountOnly, sortKeyColumnCount, captureSortValue);
        }

        /// <summary>
        /// Evaluates the eligibility gates shared by observation and execution. Returns <see langword="true"/>
        /// when the request may be handled by Ignixa; otherwise logs the specific skip reason and returns
        /// <see langword="false"/>. The gate order and log messages are load-bearing and asserted by tests.
        /// </summary>
        private bool IsEligible(SqlSearchOptions searchOptions, bool accessControlPredicateRequired)
        {
            if (searchOptions.IgnixaOptions == null)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "null-ignixa-options");
                return false;
            }

            if (!searchOptions.ResourceVersionTypes.HasFlag(ResourceVersionType.Latest))
            {
                // History-only and SoftDeleted-only requests render in legacy as an exact IsHistory=1 /
                // IsDeleted=1 filter, which the compiler's relaxation-only ResourceVisibility cannot express.
                // Only Latest-inclusive combinations map faithfully, so keep the rest on the legacy path.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "non-latest-version-type");
                return false;
            }

            if (searchOptions.IsAsyncOperation)
            {
                // Async operations (export/bulk) take the client _count verbatim as MaxItemCount and fold the
                // surrogate-id predicate into the legacy parameter hash (see ResourceSurrogateIdParameterQueryGenerator).
                // These are legacy plan-shaping and job-resumption concerns, not a compiler capability, and are not
                // exercised by the differential suite. Keep async operations on the legacy path until that agreement
                // can be proven.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "async-operation");
                return false;
            }

            if (searchOptions.FeedRange != null)
            {
                // FeedRange is a Cosmos physical-partition token consumed only by FhirCosmosSearchService; the SQL
                // search service never reads it and GetFeedRanges is unimplemented for SQL, so this gate is inert on
                // the SQL path. It is not a surrogate-id range, so it cannot be mapped to the compiler's SurrogateRange.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "feed-range");
                return false;
            }

            if (searchOptions.ContinuationToken != null && !IsSurrogateKeysetContinuation(searchOptions))
            {
                // Keyset pagination is wired only for the default surrogate-id order: a row-returning search
                // with no custom _sort whose token carries a composite (ResourceTypeId, ResourceSurrogateId)
                // boundary and no captured sort value. That subset maps to a PageSpec whose forward tuple seek
                // matches the legacy GreaterThan on the partitioned primary key. Custom-sort tokens, count-only
                // tokens, and legacy type-less tokens still render a boundary this pass does not reproduce, so
                // they stay on the legacy path.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "continuation-token");
                return false;
            }

            if (searchOptions.IncludesContinuationToken != null)
            {
                // The $includes sub-operation is a dedicated second-phase paging protocol (IsIncludesOperation path,
                // IncludesOperationRewriter, and the match surrogate-range windowing that mints IncludesContinuationToken)
                // entangled with the legacy include machinery this change must not modify. Wiring the compiler's
                // IncludesOnly capability would require Ignixa to reproduce that entire protocol and prove page-for-page
                // agreement, which is out of scope here.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "includes-continuation-token");
                return false;
            }

            if (searchOptions.UnsupportedSearchParams?.Count > 0)
            {
                _logger.LogDebug(
                    "Skipping Ignixa compile-only observation. Reason={Reason}, Count={Count}",
                    "unsupported-search-params",
                    searchOptions.UnsupportedSearchParams.Count);
                return false;
            }

            if (accessControlPredicateRequired)
            {
                // SECURITY BOUNDARY - deliberately closed. Two distinct mechanisms trip this gate, and neither can be
                // routed to Ignixa safely today:
                //   1. Fine-grained SMART clinical scopes (AccessControlContext.ApplyFineGrainedAccessControl) are built
                //      only as legacy server Expressions in SearchOptionsFactory.CheckFineGrainedAccessControl and added
                //      solely to the legacy search expression tree. They are never translated into IgnixaOptions or the
                //      compiler's AccessConstraints, so running Ignixa would apply no scope predicate at all.
                //   2. SMART compartment access (AccessControlContext.CompartmentResourceType) is ANDed into
                //      IgnixaOptions.Expression (the match filter) via AppendIgnixaCompartmentExpression, but is not
                //      registered as a structural AccessConstraint. Ignixa runs _include/_revinclude as separate
                //      row-producing stages, so a match-only predicate would leave included resources unconstrained.
                // Opening this gate without a proven per-type AccessConstraint translation and an include-path
                // enforcement test would be a half-wired authorization control, which is worse than none. Keep closed.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "access-control-predicate");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when a continuation token can be honoured by the Ignixa keyset
        /// <see cref="PageSpec"/> this pass wires: a row-returning search with no custom <c>_sort</c>, whose
        /// token parses and carries a composite (ResourceTypeId, ResourceSurrogateId) boundary with no captured
        /// sort value. This is the exact subset <see cref="IgnixaSqlCompilerAdapter"/> builds a PageSpec for, so
        /// the router and the adapter agree on which tokens route to Ignixa and which fall back to legacy.
        /// </summary>
        private static bool IsSurrogateKeysetContinuation(SqlSearchOptions searchOptions)
        {
            // Count-only requests never AND the token into the legacy tree and do not paginate rows; keep any
            // count-only + token request on the legacy path.
            if (searchOptions.CountOnly)
            {
                return false;
            }

            // A custom _sort (including _lastUpdated/_type reaching Ignixa) drives a keyed boundary the
            // surrogate-only PageSpec cannot express.
            if ((searchOptions.IgnixaOptions?.Sort?.Count ?? 0) > 0)
            {
                return false;
            }

            ContinuationToken token = ContinuationToken.FromString(searchOptions.ContinuationToken);
            return token != null
                && token.ResourceTypeId != null
                && string.IsNullOrEmpty(token.SortValue);
        }
    }
}
