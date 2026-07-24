// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
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

            if (searchOptions.IgnixaOptions == null)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "null-ignixa-options");
                return;
            }

            if (searchOptions.ResourceVersionTypes != ResourceVersionType.Latest)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "non-latest-version-type");
                return;
            }

            if (searchOptions.IgnoreSearchParamHash)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "ignore-search-param-hash");
                return;
            }

            if (searchOptions.IsAsyncOperation)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "async-operation");
                return;
            }

            if (searchOptions.FeedRange != null)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "feed-range");
                return;
            }

            if (searchOptions.ContinuationToken != null)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "continuation-token");
                return;
            }

            if (searchOptions.IncludesContinuationToken != null)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "includes-continuation-token");
                return;
            }

            if (searchOptions.UnsupportedSearchParams?.Count > 0)
            {
                _logger.LogDebug(
                    "Skipping Ignixa compile-only observation. Reason={Reason}, Count={Count}",
                    "unsupported-search-params",
                    searchOptions.UnsupportedSearchParams.Count);
                return;
            }

            if (accessControlPredicateRequired)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "access-control-predicate");
                return;
            }

            IgnixaSearchOptions ignixaOptions = searchOptions.IgnixaOptions;

            if (ignixaOptions.ResourceType == null)
            {
                _logger.LogDebug("Skipping Ignixa compile-only observation. Reason={Reason}", "null-resource-type");
                return;
            }

            if (ignixaOptions.ResourceTypes != null && ignixaOptions.ResourceTypes.Count > 1)
            {
                _logger.LogDebug(
                    "Skipping Ignixa compile-only observation. Reason={Reason}, ResourceTypeCount={Count}",
                    "multi-resource-types",
                    ignixaOptions.ResourceTypes.Count);
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
    }
}
