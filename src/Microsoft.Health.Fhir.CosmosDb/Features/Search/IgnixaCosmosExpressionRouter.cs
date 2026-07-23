// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Search
{
    /// <summary>
    /// Routes the canonical Ignixa expression through the one-way FHIR Server compatibility bridge before the
    /// Cosmos search pipeline consumes <see cref="SearchOptions.Expression"/>.
    /// </summary>
    /// <remarks>
    /// The routing logic is isolated in this type (rather than living inline in <c>FhirCosmosSearchService</c>)
    /// so that the fail-fast and divergence-guard behavior can be unit tested directly with a mocked
    /// <see cref="IIgnixaLegacyExpressionBridge"/>, without constructing the full Cosmos search service.
    /// </remarks>
    internal sealed class IgnixaCosmosExpressionRouter
    {
        private readonly IIgnixaLegacyExpressionBridge _ignixaLegacyExpressionBridge;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaCosmosExpressionRouter"/> class.
        /// </summary>
        /// <param name="ignixaLegacyExpressionBridge">The one-way Ignixa-to-FHIR-Server expression bridge.</param>
        /// <param name="logger">The logger used to record divergence between the canonical and legacy paths.</param>
        public IgnixaCosmosExpressionRouter(IIgnixaLegacyExpressionBridge ignixaLegacyExpressionBridge, ILogger logger)
        {
            EnsureArg.IsNotNull(ignixaLegacyExpressionBridge, nameof(ignixaLegacyExpressionBridge));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _ignixaLegacyExpressionBridge = ignixaLegacyExpressionBridge;
            _logger = logger;
        }

        /// <summary>
        /// Lowers the canonical Ignixa expression with <see cref="Ignixa.Search.Expressions.LegacyExpressionLowerer"/>
        /// and converts it to the FHIR Server expression model through <see cref="IIgnixaLegacyExpressionBridge"/>.
        /// When the bridged expression is structurally equivalent to the legacy projection (or no legacy projection
        /// exists) it becomes the working expression, routing Cosmos search through the bridge.
        /// </summary>
        /// <param name="searchOptions">The (already cloned) search options whose expression may be replaced in place.</param>
        /// <exception cref="SearchOperationNotSupportedException">
        /// Thrown when lowering or bridging the canonical Ignixa expression encounters a node or value that the bridge
        /// cannot faithfully represent. This is intentionally not swallowed: unsupported canonical semantics must fail
        /// before Cosmos query execution rather than silently falling back to an approximate legacy projection.
        /// </exception>
        public void Route(SearchOptions searchOptions)
        {
            EnsureArg.IsNotNull(searchOptions, nameof(searchOptions));

            if (searchOptions.IgnixaOptions?.Expression == null)
            {
                // No canonical expression to route (for example unmigrated internal search paths). This is the only
                // allowed legacy fallback: there is nothing canonical that could be masked. Keep the legacy projection.
                return;
            }

            // Lower and bridge the canonical expression. Any SearchOperationNotSupportedException raised by the lowerer
            // or the bridge propagates by design so that unsupported canonical semantics fail before Cosmos executes.
            Ignixa.Search.Expressions.Expression lowered =
                Ignixa.Search.Expressions.LegacyExpressionLowerer.LowerToLegacy(searchOptions.IgnixaOptions.Expression);
            Expression bridged = _ignixaLegacyExpressionBridge.Convert(lowered);

            Expression legacy = searchOptions.Expression;
            if (legacy != null && !IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(bridged, legacy))
            {
                // The FHIR Server request pipeline augments the legacy projection with concerns that are not present in
                // the raw Ignixa tree (for example SMART compartment scoping and fine-grained access control unions,
                // which lower to distinct node types). Retain the fully-specified legacy projection for those cases so
                // the query is never broadened, but log so the divergence is never silent. This is a successful-but-
                // different bridge result, not a swallowed failure.
                _logger.LogWarning(
                    "Ignixa bridged expression diverges structurally from the legacy projection; retaining the legacy projection. Bridged: {BridgedExpression}; Legacy: {LegacyExpression}",
                    bridged.ToString(),
                    legacy.ToString());
                return;
            }

            searchOptions.Expression = bridged;
        }
    }
}
