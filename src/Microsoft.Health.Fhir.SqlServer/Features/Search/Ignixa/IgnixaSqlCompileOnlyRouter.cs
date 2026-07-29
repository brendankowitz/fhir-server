// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Ignixa.Search.Sql.Ast;
using Ignixa.Search.Sql.Lowering;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.SqlServer.Registration;
using IgnixaSortExpression = Ignixa.Search.Expressions.SortExpression;

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
            // Plain, wildcard, and :iterate include stages are all allowed onto the Ignixa path. Wildcard
            // ("_include=*") lowers to a reference-parameter-less join and :iterate resolves its closure in a
            // single topological pass in Lower rather than legacy's fixed-point iteration; both were previously
            // guarded because that emitted row set had not been compared to legacy. It now is - see the
            // wildcard-include and iterate-include differentials in SqlServerIgnixaExecutionTests, which seed real
            // dbo.ReferenceSearchParam rows and assert the full seeded (id -> match/include) map matches legacy.
            bool hasIncludes = (plan.Includes?.Count ?? 0) > 0;

            // The sort-phase mapping in IgnixaSqlCompilerAdapter now handles the two cases legacy treats
            // specially - IsSortWithFilter and a ":missing" modifier on the sort parameter - by forcing the
            // Valued phase, which is the only phase legacy ever runs for either. See the phase derivation there
            // for why direction and SortQuerySecondPhase must be ignored in those cases.

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

            if (searchOptions.QueryHints != null)
            {
                // Unreachable by construction today, and kept as a backstop rather than removed.
                //
                // SearchOptionsFactory populates QueryHints only when the request carries a non-null
                // _globalEndSurrogateId, and SqlServerSearchService.SearchImpl routes exactly that condition
                // (ContainsGlobalEndSurrogateId) to the GetResourcesByTypeAndSurrogateIdRange stored procedure
                // before the router is consulted. Reindex does not reach SearchImpl at all - it has its own
                // surrogate-range path. So no hint-carrying request can arrive here.
                //
                // The gate is on the hints rather than on IsAsyncOperation because the flag by itself is inert
                // for Ignixa: MaxItemCount is resolved onto the shared SearchOptions, and the only other legacy
                // effect - ResourceSurrogateIdParameterQueryGenerator folding the surrogate-id predicate into the
                // query-plan-reuse hash - belongs to HashingSqlQueryParameterManager, which Ignixa does not use
                // because it emits its own parameters. Ordinary async pages therefore route.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "query-hints");
                return false;
            }

            if (!searchOptions.IgnixaSortAgreesWithLegacy)
            {
                // Ignixa binds _sort itself, while SearchOptionsFactory runs the same parameter past the storage
                // layer's sorting validator, which discards the whole sort when SQL cannot honour it - a token or
                // reference sort, or any multi-key shape other than (_type, _lastUpdated). When that happens
                // legacy returns rows in surrogate order and Ignixa would return them sorted, so the two engines
                // would disagree on ordering, and on a paged search on which rows appear at all. The comparison
                // itself lives in SearchOptionsFactory because SqlServerSearchService has already rewritten
                // SearchOptions.Sort into its two-column form by the time the router runs.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "sort-disagreement");
                return false;
            }

            if (searchOptions.ContinuationToken != null && !IsKeysetContinuation(searchOptions, out string keysetSkipReason))
            {
                // Keyset pagination is wired for the default surrogate-id order and for a single-key custom
                // _sort, in both sort phases, and count-only pages route because neither engine paginates them.
                // What remains unwired is a token with no ResourceTypeId slot on a multi-type search - there is
                // no single constant type to substitute for the boundary the token omitted, and legacy tie-breaks
                // such a search on Sid1 alone where Ignixa orders (T1, Sid1) - and a multi-key sort, whose second
                // boundary value the token never captured.
                _logger.LogDebug(
                    "Skipping Ignixa compile-only observation. Reason={Reason}, Detail={Detail}",
                    "continuation-token",
                    keysetSkipReason);
                return false;
            }

            if (searchOptions.IncludesContinuationToken != null && !IsIncludesOperationEligible(searchOptions, out string includesSkipReason))
            {
                // The $includes sub-operation now routes to Ignixa's IncludesOnly capability for the plain
                // (unsorted) include page: SqlServerSearchService.SearchIncludeImpl consults this router, and the
                // adapter translates the IncludesContinuationToken's match window and resume point into an
                // includes-only plan. What remains on legacy is the sorted two-phase protocol (SortQuerySecondPhase
                // / SecondPhaseContinuationToken) and count-only includes; see IsIncludesOperationEligible and
                // IgnixaSqlCompilerAdapter.TryBuildIncludesOnlyWindow for why those shapes are deferred.
                _logger.LogDebug(
                    "Skipping Ignixa compile-only observation. Reason={Reason}, Detail={Detail}",
                    "includes-continuation-token",
                    includesSkipReason);
                return false;
            }

            if (searchOptions.UnsupportedSearchParams?.Count > 0 && !searchOptions.IgnixaUnsupportedParamsAgreeWithLegacy)
            {
                // Both engines drop parameters they cannot honour and report them back on the bundle, so an
                // unsupported parameter is not on its own a reason to avoid Ignixa. A *disagreement* is: a
                // parameter Ignixa dropped that legacy applied makes the Ignixa result a superset of the correct
                // rows, and the reverse makes it a subset. SearchOptionsFactory compares the two drop sets and
                // only sets the flag when they match exactly.
                _logger.LogDebug(
                    "Skipping Ignixa compile-only observation. Reason={Reason}, Count={Count}",
                    "unsupported-search-params",
                    searchOptions.UnsupportedSearchParams.Count);
                return false;
            }

            if (searchOptions.IgnixaSmartCompartmentSearch && !searchOptions.IgnixaSmartCompartmentTranslated)
            {
                // SECURITY BOUNDARY. Part of the SMART membership rule has no Ignixa spelling for this request.
                // The union expands to up to five legs (compartment membership, the user's own resource, universal
                // types, orphan devices, this patient's devices) and a union missing a leg admits a different set
                // than legacy does, so anything the translator could not express keeps the legacy path.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "smart-compartment-definition");
                return false;
            }

            if (accessControlPredicateRequired && !searchOptions.IgnixaAccessControlTranslated)
            {
                // SECURITY BOUNDARY. The request carries an access control predicate that
                // SearchOptionsFactory.TranslateClinicalScopesForIgnixa did not fully translate into IgnixaOptions,
                // so the compiler would enforce less than the legacy path does — or nothing at all. What remains
                // untranslated is a scope predicate Ignixa could not parse: dropping the term it did not
                // understand would widen what the caller may see, so the request keeps the legacy path.
                //
                // The condition is deliberately "not translated" rather than an enumeration of unsupported shapes,
                // so a future access control mechanism that nobody teaches the translator about fails closed here
                // instead of silently routing unenforced.
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "access-control-predicate");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when a <c>$includes</c> request may be served by Ignixa's
        /// <c>IncludesOnly</c> capability, or <see langword="false"/> with a <paramref name="skipReason"/> for a
        /// shape still owned by the legacy include machinery. This mirrors
        /// <c>IgnixaSqlCompilerAdapter.TryBuildIncludesOnlyWindow</c> so the router and the adapter agree on
        /// which include pages route.
        /// </summary>
        private static bool IsIncludesOperationEligible(SqlSearchOptions searchOptions, out string skipReason)
        {
            skipReason = null;

            // A count-only $includes is contradictory - Lower rejects IncludesOnly with CountOnly and legacy
            // never counts included resources - so it stays on legacy.
            if (searchOptions.CountOnly)
            {
                skipReason = "count-only";
                return false;
            }

            IncludesContinuationToken token = IncludesContinuationToken.FromString(searchOptions.IncludesContinuationToken);
            if (token == null)
            {
                // An unparseable includes token is a legacy BadRequest, not an Ignixa page.
                skipReason = "unparseable-token";
                return false;
            }

            // Deferred: the sorted two-phase $includes protocol. The cross-phase match reconstruction behind
            // SortQuerySecondPhase / SecondPhaseContinuationToken has no Ignixa spelling yet, so those pages keep
            // the legacy path.
            if (token.SortQuerySecondPhase == true || token.SecondPhaseContinuationToken != null)
            {
                skipReason = "second-phase-sort";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when a continuation token can be honoured by the Ignixa keyset
        /// <see cref="PageSpec"/>: a row-returning search whose token parses and carries a composite
        /// (ResourceTypeId, ResourceSurrogateId) boundary, sorted by at most one custom key. This is the exact
        /// subset <see cref="IgnixaSqlCompilerAdapter"/> builds a PageSpec for, so the router and the adapter
        /// agree on which tokens route to Ignixa and which fall back to legacy.
        /// </summary>
        private static bool IsKeysetContinuation(SqlSearchOptions searchOptions, out string skipReason)
        {
            skipReason = null;

            // A count-only request does not paginate: legacy gates its entire continuation-token block on
            // !CountOnly, so the token is neither seeked past nor validated, and the adapter drops it for the
            // same reason. Nothing below can disagree, including an unparseable token, which legacy also
            // tolerates here where it would reject it on a row-returning page.
            if (searchOptions.CountOnly)
            {
                return true;
            }

            // A multi-key sort's second boundary value is not in the token - but that only matters when a key
            // needs one. The SQL sorting validator admits exactly one multi-key shape, (_type, _lastUpdated),
            // and both of those are resource columns whose boundary *is* the token's (ResourceTypeId,
            // ResourceSurrogateId) identity pair. So the constraint is on sort-value keys, not on key count.
            IReadOnlyList<IgnixaSortExpression> sort = searchOptions.IgnixaOptions?.Sort;
            if (sort != null && sort.Count > 1 && sort.Any(s => !ResourceColumnLoweringRule.IsResourceColumnCode(s.Parameter.Code)))
            {
                skipReason = "multi-key-sort";
                return false;
            }

            ContinuationToken token = ContinuationToken.FromString(searchOptions.ContinuationToken);
            if (token == null)
            {
                skipReason = "unparseable";
                return false;
            }

            // The "second phase" sentinel token carries no real boundary - legacy discards it and restarts the
            // other sort segment from the top, and the adapter does the same - so it is eligible despite
            // failing the ResourceTypeId check below.
            if (token.ResourceSurrogateId == 0 &&
                string.Equals(token.SortValue, SqlSearchConstants.SortSentinelValueForCt, StringComparison.Ordinal))
            {
                return true;
            }

            if (token.ResourceTypeId == null && !IsSingleTypeScoped(searchOptions))
            {
                // A custom-sort token is [sortValue, surrogateId] - it never carries a ResourceTypeId slot.
                // For a single-type search the adapter substitutes the search's own type, but a multi-type one
                // has no type to substitute, so the seek has to omit the type boundary entirely. Ignixa can do
                // that only for a custom (search-parameter) sort key, whose ORDER BY it emits with no type term
                // - sound because ResourceSurrogateId is globally unique. Every other shape orders type-major
                // and a surrogate-only seek would drop rows across the page seam.
                bool hasSearchParameterSortKey = sort != null
                    && sort.Count > 0
                    && sort.Any(s => !ResourceColumnLoweringRule.IsResourceColumnCode(s.Parameter.Code));

                if (!hasSearchParameterSortKey)
                {
                    skipReason = "no-resource-type-id";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when the search targets exactly one resource type, so the compiled
        /// row set carries a single constant ResourceTypeId that can stand in for a boundary the token omits.
        /// </summary>
        private static bool IsSingleTypeScoped(SqlSearchOptions searchOptions)
        {
            var ignixaOptions = searchOptions.IgnixaOptions;

            return ignixaOptions != null
                && !string.IsNullOrEmpty(ignixaOptions.ResourceType)
                && (ignixaOptions.ResourceTypes?.Count ?? 0) <= 1;
        }
    }
}
