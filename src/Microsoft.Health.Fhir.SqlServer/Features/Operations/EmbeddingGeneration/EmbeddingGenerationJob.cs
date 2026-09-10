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
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.EmbeddingGeneration;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Features.Search.VectorSearch;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.JobManagement;
using Newtonsoft.Json;

namespace Microsoft.Health.Fhir.SqlServer.Features.Operations.EmbeddingGeneration
{
    [JobTypeId((int)JobType.EmbeddingGenerationProcessing)]
    internal class EmbeddingGenerationJob : IJob
    {
        private readonly Func<IScoped<ISearchService>> _searchServiceFactory;
        private readonly ISearchIndexer _searchIndexer;
        private readonly IResourceDeserializer _resourceDeserializer;
        private readonly IVectorSearchParamStore _vectorSearchParamStore;
        private readonly IEmbeddingClient _embeddingClient;
        private readonly IVectorSearchCapabilityProvider _capabilityProvider;
        private readonly ISqlServerFhirModel _model;
        private readonly RequestContextAccessor<IFhirRequestContext> _contextAccessor;
        private readonly ILogger<EmbeddingGenerationJob> _logger;

        public EmbeddingGenerationJob(
            Func<IScoped<ISearchService>> searchServiceFactory,
            ISearchIndexer searchIndexer,
            IResourceDeserializer resourceDeserializer,
            IVectorSearchParamStore vectorSearchParamStore,
            IEmbeddingClient embeddingClient,
            IVectorSearchCapabilityProvider capabilityProvider,
            ISqlServerFhirModel model,
            RequestContextAccessor<IFhirRequestContext> contextAccessor,
            ILoggerFactory loggerFactory)
        {
            _searchServiceFactory = EnsureArg.IsNotNull(searchServiceFactory, nameof(searchServiceFactory));
            _searchIndexer = EnsureArg.IsNotNull(searchIndexer, nameof(searchIndexer));
            _resourceDeserializer = EnsureArg.IsNotNull(resourceDeserializer, nameof(resourceDeserializer));
            _vectorSearchParamStore = EnsureArg.IsNotNull(vectorSearchParamStore, nameof(vectorSearchParamStore));
            _embeddingClient = EnsureArg.IsNotNull(embeddingClient, nameof(embeddingClient));
            _capabilityProvider = EnsureArg.IsNotNull(capabilityProvider, nameof(capabilityProvider));
            _model = EnsureArg.IsNotNull(model, nameof(model));
            _contextAccessor = EnsureArg.IsNotNull(contextAccessor, nameof(contextAccessor));
            _logger = EnsureArg.IsNotNull(loggerFactory, nameof(loggerFactory)).CreateLogger<EmbeddingGenerationJob>();
        }

        public async Task<string> ExecuteAsync(JobInfo jobInfo, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobInfo, nameof(jobInfo));

            EmbeddingGenerationJobDefinition definition = jobInfo.DeserializeDefinition<EmbeddingGenerationJobDefinition>();
            var result = new EmbeddingGenerationJobResult();
            _contextAccessor.RequestContext = new FhirRequestContext(
                method: "EmbeddingGeneration",
                uriString: "http://localhost/$embedding-generation",
                baseUriString: "http://localhost/",
                correlationId: jobInfo.GroupId.ToString(),
                requestHeaders: new Dictionary<string, StringValues>(),
                responseHeaders: new Dictionary<string, StringValues>())
            {
                IsBackgroundTask = true,
            };

            await _capabilityProvider.EnsureInitializedAsync(cancellationToken);
            if (!_capabilityProvider.IsVectorSearchAvailable)
            {
                _logger.LogInformation(
                    "Embedding generation job {JobId} skipped because vector search is unavailable.",
                    jobInfo.Id);
                return JsonConvert.SerializeObject(result);
            }

            string resourceType = _model.GetResourceTypeName(definition.ResourceTypeId);
            List<ResourceWrapper> resources;
            using (IScoped<ISearchService> searchService = _searchServiceFactory())
            {
                SearchResult searchResult = await searchService.Value.SearchBySurrogateIdRange(
                    resourceType,
                    definition.StartSurrogateId,
                    definition.EndSurrogateId,
                    cancellationToken);
                resources = searchResult.Results.Select(entry => entry.Resource).ToList();
            }

            result.ResourcesProcessed = resources.Count;

            var extractedByResource = new Dictionary<long, IReadOnlyList<ExtractedVectorValue>>();
            foreach (ResourceWrapper resource in resources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyCollection<SearchIndexEntry> searchIndices =
                    _searchIndexer.Extract(_resourceDeserializer.Deserialize(resource));
                resource.UpdateSearchIndices(searchIndices);
                extractedByResource[resource.ResourceSurrogateId] = searchIndices
                    .Where(entry => entry.Value is VectorSearchValue)
                    .Select(entry => new ExtractedVectorValue(
                        _model.GetSearchParamId(entry.SearchParameter.Url),
                        (VectorSearchValue)entry.Value))
                    .ToList();
            }

            IReadOnlyDictionary<long, IReadOnlyList<VectorSearchParamState>> storedState =
                await _vectorSearchParamStore.GetStateAsync(
                    definition.ResourceTypeId,
                    definition.StartSurrogateId,
                    definition.EndSurrogateId,
                    cancellationToken);
            short embeddingModelId = _capabilityProvider.EmbeddingModelId;
            var changedResources = new List<ResourceWrapper>();
            var pendingValues = new List<VectorSearchValue>();

            foreach (ResourceWrapper resource in resources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<ExtractedVectorValue> extractedValues = extractedByResource[resource.ResourceSurrogateId];
                var extractedSet = extractedValues
                    .Select(value => new VectorStateKey(
                        value.SearchParamId,
                        checked((short)value.Value.ChunkOrdinal),
                        value.Value.SourceTextHash,
                        embeddingModelId))
                    .ToHashSet();
                var storedSet = storedState.TryGetValue(resource.ResourceSurrogateId, out IReadOnlyList<VectorSearchParamState> states)
                    ? states.Select(state => new VectorStateKey(
                        state.SearchParamId,
                        state.ChunkOrdinal,
                        state.SourceTextHash,
                        state.EmbeddingModelId)).ToHashSet()
                    : new HashSet<VectorStateKey>();

                if (extractedSet.SetEquals(storedSet))
                {
                    result.ResourcesSkipped++;
                    continue;
                }

                changedResources.Add(resource);
                if (extractedValues.Count > 0)
                {
                    result.ResourcesEmbedded++;
                    pendingValues.AddRange(extractedValues.Select(value => value.Value));
                }
            }

            if (pendingValues.Count > 0)
            {
                IReadOnlyList<float[]> embeddings = await _embeddingClient.EmbedAsync(
                    pendingValues.Select(value => value.SourceText).ToList(),
                    tightBudget: false,
                    cancellationToken);

                if (embeddings == null || embeddings.Count != pendingValues.Count)
                {
                    _logger.LogError(
                        "The embedding client returned {EmbeddingCount} embeddings for {InputCount} inputs.",
                        embeddings?.Count ?? 0,
                        pendingValues.Count);
                    throw new InvalidOperationException(
                        $"The embedding client returned {embeddings?.Count ?? 0} embeddings for {pendingValues.Count} inputs.");
                }

                for (int i = 0; i < pendingValues.Count; i++)
                {
                    pendingValues[i].SetEmbedding(embeddings[i]);
                }
            }

            if (changedResources.Count > 0)
            {
                await _vectorSearchParamStore.ReplaceAsync(definition.ResourceTypeId, changedResources, cancellationToken);
            }

            result.RowsWritten = pendingValues.Count;
            jobInfo.Data = result.ResourcesProcessed;

            return JsonConvert.SerializeObject(result);
        }

        private readonly struct ExtractedVectorValue
        {
            internal ExtractedVectorValue(short searchParamId, VectorSearchValue value)
            {
                SearchParamId = searchParamId;
                Value = value;
            }

            internal short SearchParamId { get; }

            internal VectorSearchValue Value { get; }
        }

        private readonly struct VectorStateKey : IEquatable<VectorStateKey>
        {
            internal VectorStateKey(short searchParamId, short chunkOrdinal, byte[] sourceTextHash, short embeddingModelId)
            {
                SearchParamId = searchParamId;
                ChunkOrdinal = chunkOrdinal;
                SourceTextHash = sourceTextHash;
                EmbeddingModelId = embeddingModelId;
            }

            private short SearchParamId { get; }

            private short ChunkOrdinal { get; }

            private byte[] SourceTextHash { get; }

            private short EmbeddingModelId { get; }

            public bool Equals(VectorStateKey other)
            {
                return SearchParamId == other.SearchParamId
                    && ChunkOrdinal == other.ChunkOrdinal
                    && EmbeddingModelId == other.EmbeddingModelId
                    && ByteArraysEqual(SourceTextHash, other.SourceTextHash);
            }

            public override bool Equals(object obj) => obj is VectorStateKey other && Equals(other);

            public override int GetHashCode()
            {
                HashCode hashCode = default;
                hashCode.Add(SearchParamId);
                hashCode.Add(ChunkOrdinal);
                hashCode.Add(EmbeddingModelId);
                if (SourceTextHash != null)
                {
                    foreach (byte value in SourceTextHash)
                    {
                        hashCode.Add(value);
                    }
                }

                return hashCode.ToHashCode();
            }

            private static bool ByteArraysEqual(byte[] left, byte[] right)
            {
                return ReferenceEquals(left, right)
                    || (left != null && right != null && left.AsSpan().SequenceEqual(right));
            }
        }

        private sealed class EmbeddingGenerationJobResult
        {
            public int ResourcesProcessed { get; set; }

            public int ResourcesSkipped { get; set; }

            public int ResourcesEmbedded { get; set; }

            public int RowsWritten { get; set; }
        }
    }
}
