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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Features.Search.VectorSearch;

namespace Microsoft.Health.Fhir.SqlServer.Features.Storage
{
    /// <summary>
    /// Applies vector embeddings to resource search values before SQL persistence.
    /// </summary>
    public sealed class VectorEmbeddingEnricher
    {
        private readonly IEmbeddingClient _embeddingClient;
        private readonly IVectorSearchCapabilityProvider _capabilityProvider;
        private readonly VectorSearchConfiguration _vectorSearchConfiguration;
        private readonly RequestContextAccessor<IFhirRequestContext> _requestContextAccessor;
        private readonly ILogger<VectorEmbeddingEnricher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="VectorEmbeddingEnricher"/> class.
        /// </summary>
        /// <param name="embeddingClient">The embedding client.</param>
        /// <param name="capabilityProvider">The vector search capability provider.</param>
        /// <param name="vectorSearchConfiguration">The vector search configuration.</param>
        /// <param name="requestContextAccessor">The FHIR request context accessor.</param>
        /// <param name="logger">The logger.</param>
        public VectorEmbeddingEnricher(
            IEmbeddingClient embeddingClient,
            IVectorSearchCapabilityProvider capabilityProvider,
            IOptions<VectorSearchConfiguration> vectorSearchConfiguration,
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor,
            ILogger<VectorEmbeddingEnricher> logger)
        {
            _embeddingClient = EnsureArg.IsNotNull(embeddingClient, nameof(embeddingClient));
            _capabilityProvider = EnsureArg.IsNotNull(capabilityProvider, nameof(capabilityProvider));
            _vectorSearchConfiguration = EnsureArg.IsNotNull(vectorSearchConfiguration, nameof(vectorSearchConfiguration)).Value;
            _requestContextAccessor = EnsureArg.IsNotNull(requestContextAccessor, nameof(requestContextAccessor));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <summary>
        /// Applies embeddings to all pending vector search values in a resource merge.
        /// </summary>
        /// <param name="resources">The resource operations to enrich.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>True when inline embedding was fully applied; otherwise, false.</returns>
        public async Task<bool> EnrichAsync(IReadOnlyList<ResourceWrapperOperation> resources, CancellationToken cancellationToken)
        {
            if (resources == null || resources.Count == 0)
            {
                return false;
            }

            await _capabilityProvider.EnsureInitializedAsync(cancellationToken);
            if (!_capabilityProvider.IsVectorSearchAvailable)
            {
                return false;
            }

            if (!_vectorSearchConfiguration.Enabled)
            {
                return false;
            }

            bool syncMode = _vectorSearchConfiguration.EmbeddingPersistenceMode == EmbeddingPersistenceMode.Synchronous;
            bool prefersSynchronous = RequestPrefersSynchronous();
            if (!syncMode && !prefersSynchronous)
            {
                return false;
            }

            List<VectorSearchValue> pending = resources
                .SelectMany(resource => resource.Wrapper.SearchIndices ?? Array.Empty<SearchIndexEntry>())
                .Select(entry => entry.Value)
                .OfType<VectorSearchValue>()
                .Where(value => value.Embedding == null)
                .ToList();

            if (pending.Count == 0)
            {
                ApplySynchronousEmbeddingPreference(prefersSynchronous);
                return true;
            }

            try
            {
                IReadOnlyList<float[]> embeddings = await _embeddingClient.EmbedAsync(
                    pending.Select(value => value.SourceText).ToList(),
                    tightBudget: !syncMode,
                    cancellationToken);

                if (embeddings == null || embeddings.Count != pending.Count)
                {
                    _logger.LogError(
                        "The embedding client returned {EmbeddingCount} embeddings for {InputCount} inputs.",
                        embeddings?.Count ?? 0,
                        pending.Count);
                    throw new InvalidOperationException(
                        $"The embedding client returned {embeddings?.Count ?? 0} embeddings for {pending.Count} inputs.");
                }

                for (int i = 0; i < pending.Count; i++)
                {
                    pending[i].SetEmbedding(embeddings[i]);
                }

                ApplySynchronousEmbeddingPreference(prefersSynchronous);
                return true;
            }
            catch (Exception exception) when (!syncMode && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning(
                    exception,
                    "Best-effort synchronous vector embedding was not applied; asynchronous processing will embed the resources later.");
                return false;
            }
        }

        private bool RequestPrefersSynchronous() => _requestContextAccessor.GetPrefersSynchronousEmbedding();

        private void ApplySynchronousEmbeddingPreference(bool prefersSynchronous)
        {
            if (!prefersSynchronous)
            {
                return;
            }

            IFhirRequestContext fhirRequestContext = _requestContextAccessor.RequestContext;
            if (fhirRequestContext?.ResponseHeaders == null)
            {
                return;
            }

            try
            {
                lock (fhirRequestContext.ResponseHeaders)
                {
                    fhirRequestContext.ResponseHeaders[KnownHeaders.PreferenceApplied] = "embedding-persistence=synchronous";
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Error setting the applied embedding persistence preference in response headers.");
            }
        }
    }
}
