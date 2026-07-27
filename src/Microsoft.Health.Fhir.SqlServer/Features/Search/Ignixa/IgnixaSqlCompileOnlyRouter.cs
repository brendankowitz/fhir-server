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
using IgnixaSearchOptions = Ignixa.Search.Models.SearchOptions;

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

            // Materialisation guard for includes. Includes require the include-truncation and
            // includes-continuation interplay that has not been validated against the emitted UNION ALL shape
            // yet, so a plan carrying includes still falls back to legacy for now.
            bool hasIncludes = (plan.Includes?.Count ?? 0) > 0;
            if (hasIncludes)
            {
                _logger.LogDebug("Falling back to legacy SQL: Ignixa plan carries includes. Reason={Reason}", "includes");
                return null;
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

            if (searchOptions.ResourceVersionTypes != ResourceVersionType.Latest)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "non-latest-version-type");
                return false;
            }

            if (searchOptions.IgnoreSearchParamHash)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "ignore-search-param-hash");
                return false;
            }

            if (searchOptions.IsAsyncOperation)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "async-operation");
                return false;
            }

            if (searchOptions.FeedRange != null)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "feed-range");
                return false;
            }

            if (searchOptions.ContinuationToken != null)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "continuation-token");
                return false;
            }

            if (searchOptions.IncludesContinuationToken != null)
            {
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
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "access-control-predicate");
                return false;
            }

            IgnixaSearchOptions ignixaOptions = searchOptions.IgnixaOptions;

            if (ignixaOptions.ResourceType == null)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "null-resource-type");
                return false;
            }

            if (ignixaOptions.ResourceTypes != null && ignixaOptions.ResourceTypes.Count > 1)
            {
                _logger.LogDebug(
                    "Skipping Ignixa compile-only observation. Reason={Reason}, ResourceTypeCount={Count}",
                    "multi-resource-types",
                    ignixaOptions.ResourceTypes.Count);
                return false;
            }

            return true;
        }
    }
}
