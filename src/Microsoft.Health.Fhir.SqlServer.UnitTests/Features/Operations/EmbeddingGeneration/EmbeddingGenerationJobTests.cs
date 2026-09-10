// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.ElementModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Features;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.EmbeddingGeneration;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Features.Search.VectorSearch;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer.Features.Operations.EmbeddingGeneration;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.Health.JobManagement;
using Microsoft.Health.Test.Utilities;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Operations.EmbeddingGeneration
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class EmbeddingGenerationJobTests
    {
        private const short ResourceTypeId = 1;
        private const short SearchParamId = 2;
        private const short EmbeddingModelId = 3;
        private const long ResourceSurrogateId = 100;
        private static readonly Uri SearchParameterUri = new("http://example.org/SearchParameter/vector");

        private readonly ISearchService _searchService = Substitute.For<ISearchService>();
        private readonly ISearchIndexer _searchIndexer = Substitute.For<ISearchIndexer>();
        private readonly IResourceDeserializer _resourceDeserializer = Substitute.For<IResourceDeserializer>();
        private readonly TestVectorSearchParamStore _vectorSearchParamStore = new();
        private readonly IEmbeddingClient _embeddingClient = Substitute.For<IEmbeddingClient>();
        private readonly IVectorSearchCapabilityProvider _capabilityProvider = Substitute.For<IVectorSearchCapabilityProvider>();
        private readonly ISqlServerFhirModel _model = Substitute.For<ISqlServerFhirModel>();
        private readonly RequestContextAccessor<IFhirRequestContext> _contextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
        private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
        private readonly IScoped<ISearchService> _searchServiceScope = Substitute.For<IScoped<ISearchService>>();
        private readonly IVectorSearchMetricHandler _metricHandler = Substitute.For<IVectorSearchMetricHandler>();

        public EmbeddingGenerationJobTests()
        {
            _searchServiceScope.Value.Returns(_searchService);
            _capabilityProvider.IsVectorSearchAvailable.Returns(true);
            _capabilityProvider.EmbeddingModelId.Returns(EmbeddingModelId);
            _capabilityProvider.EnsureInitializedAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            _model.GetResourceTypeName(ResourceTypeId).Returns("Patient");
            _model.GetSearchParamId(SearchParameterUri).Returns(SearchParamId);
        }

        [Fact]
        public async Task GivenMatchingStoredState_WhenExecuted_ThenSkipsEmbeddingAndReplacement()
        {
            // Arrange
            ResourceWrapper resource = CreateResource();
            VectorSearchValue value = ConfigureResource(resource, "same", 0);
            ConfigureStoredState(resource, new VectorSearchParamState(SearchParamId, 0, value.SourceTextHash, EmbeddingModelId));
            EmbeddingGenerationJob job = CreateJob();

            // Act
            string result = await job.ExecuteAsync(CreateJobInfo(), CancellationToken.None);

            // Assert
            EmbeddingGenerationJobResult parsedResult = JsonConvert.DeserializeObject<EmbeddingGenerationJobResult>(result);
            Assert.Equal(1, parsedResult.ResourcesProcessed);
            Assert.Equal(1, parsedResult.ResourcesSkipped);
            Assert.Equal(0, parsedResult.ResourcesEmbedded);
            Assert.Equal(0, parsedResult.RowsWritten);
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
            Assert.Equal(0, _vectorSearchParamStore.ReplaceCallCount);
            _metricHandler.Received(1).EmitBackgroundJob(
                Arg.Is<VectorBackgroundJobMetricNotification>(notification =>
                    notification.RowsWritten == 0 &&
                    notification.ResourcesSkipped == 1));
        }

        [Fact]
        public async Task GivenEqualHashContentsInDifferentArrays_WhenExecuted_ThenSkipsEmbedding()
        {
            // Arrange
            ResourceWrapper resource = CreateResource();
            VectorSearchValue value = ConfigureResource(resource, "same", 0);
            byte[] separatelyAllocatedHash = value.SourceTextHash.ToArray();
            Assert.NotSame(value.SourceTextHash, separatelyAllocatedHash);
            ConfigureStoredState(resource, new VectorSearchParamState(SearchParamId, 0, separatelyAllocatedHash, EmbeddingModelId));
            EmbeddingGenerationJob job = CreateJob();

            // Act
            await job.ExecuteAsync(CreateJobInfo(), CancellationToken.None);

            // Assert
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
            Assert.Equal(0, _vectorSearchParamStore.ReplaceCallCount);
        }

        [Fact]
        public async Task GivenChangedText_WhenExecuted_ThenEmbedsAndReplacesResource()
        {
            // Arrange
            ResourceWrapper resource = CreateResource();
            VectorSearchValue value = ConfigureResource(resource, "new text", 0);
            ConfigureStoredState(resource, new VectorSearchParamState(SearchParamId, 0, new VectorSearchValue("old text", 0).SourceTextHash, EmbeddingModelId));
            float[] embedding = CreateEmbedding();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { embedding });
            EmbeddingGenerationJob job = CreateJob();

            // Act
            await job.ExecuteAsync(CreateJobInfo(), CancellationToken.None);

            // Assert
            Assert.Same(embedding, value.Embedding);
            await _embeddingClient.Received(1).EmbedAsync(
                Arg.Is<IReadOnlyList<string>>(inputs => inputs.SequenceEqual(new[] { "new text" })),
                false,
                CancellationToken.None);
            Assert.Equal(1, _vectorSearchParamStore.ReplaceCallCount);
            Assert.Same(resource, Assert.Single(_vectorSearchParamStore.ReplacedResources));
            _metricHandler.Received(1).EmitEmbedding(
                Arg.Is<VectorEmbeddingMetricNotification>(notification =>
                    notification.Operation == VectorEmbeddingOperation.BackgroundWrite &&
                    notification.Outcome == VectorEmbeddingOutcome.Success &&
                    notification.ElapsedMilliseconds >= 0));
            _metricHandler.Received(1).EmitBackgroundJob(
                Arg.Is<VectorBackgroundJobMetricNotification>(notification =>
                    notification.RowsWritten == 1 &&
                    notification.ResourcesSkipped == 0));
        }

        [Fact]
        public async Task GivenResourceNowHasNoVectorValues_WhenExecuted_ThenReplacesResourceToDeleteStoredRows()
        {
            // Arrange
            ResourceWrapper resource = CreateResource();
            ConfigureResource(resource);
            ConfigureStoredState(resource, new VectorSearchParamState(SearchParamId, 0, new byte[] { 1 }, EmbeddingModelId));
            EmbeddingGenerationJob job = CreateJob();

            // Act
            await job.ExecuteAsync(CreateJobInfo(), CancellationToken.None);

            // Assert
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
            Assert.Equal(1, _vectorSearchParamStore.ReplaceCallCount);
            Assert.Same(resource, Assert.Single(_vectorSearchParamStore.ReplacedResources));
        }

        [Theory]
        [InlineData(2, 1)]
        [InlineData(1, 2)]
        public async Task GivenChunkCountChanged_WhenExecuted_ThenEmbedsAndReplacesResource(int storedChunkCount, int extractedChunkCount)
        {
            // Arrange
            ResourceWrapper resource = CreateResource();
            VectorSearchValue[] extractedValues = Enumerable.Range(0, extractedChunkCount)
                .Select(index => new VectorSearchValue($"chunk {index}", index))
                .ToArray();
            ConfigureResource(resource, extractedValues);
            ConfigureStoredState(
                resource,
                Enumerable.Range(0, storedChunkCount)
                    .Select(index => new VectorSearchParamState(
                        SearchParamId,
                        (short)index,
                        new VectorSearchValue($"chunk {index}", index).SourceTextHash,
                        EmbeddingModelId))
                    .ToArray());
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(extractedValues.Select(_ => CreateEmbedding()).ToArray());
            EmbeddingGenerationJob job = CreateJob();

            // Act
            await job.ExecuteAsync(CreateJobInfo(), CancellationToken.None);

            // Assert
            await _embeddingClient.Received(1).EmbedAsync(
                Arg.Is<IReadOnlyList<string>>(inputs => inputs.Count == extractedChunkCount),
                false,
                CancellationToken.None);
            Assert.Equal(1, _vectorSearchParamStore.ReplaceCallCount);
            Assert.Same(resource, Assert.Single(_vectorSearchParamStore.ReplacedResources));
        }

        [Fact]
        public async Task GivenEmbeddingFailure_WhenExecuted_ThenPropagatesException()
        {
            // Arrange
            ResourceWrapper resource = CreateResource();
            ConfigureResource(resource, "new text", 0);
            ConfigureStoredState(resource);
            var expectedException = new InvalidOperationException("embedding failed");
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<IReadOnlyList<float[]>>(expectedException));
            EmbeddingGenerationJob job = CreateJob();

            // Act
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => job.ExecuteAsync(CreateJobInfo(), CancellationToken.None));

            // Assert
            Assert.Same(expectedException, exception);
            Assert.Equal(0, _vectorSearchParamStore.ReplaceCallCount);
        }

        [Fact]
        public async Task GivenMultipleChangedResourcesWithUnevenChunkCounts_WhenEmbeddingReturnsDistinctVectors_ThenEachVectorIsAssignedToItsOriginalResourceAndChunk()
        {
            // Arrange
            ResourceWrapper firstResource = CreateResource(ResourceSurrogateId);
            VectorSearchValue firstChunk = new("first chunk", 0);
            VectorSearchValue secondChunk = new("second chunk", 1);
            ConfigureResource(firstResource, firstChunk, secondChunk);
            ResourceWrapper secondResource = CreateResource(ResourceSurrogateId + 1);
            VectorSearchValue thirdChunk = new("third chunk", 0);
            ConfigureResource(secondResource, thirdChunk);
            ConfigureSearchResults(firstResource, secondResource);
            ConfigureStoredState();
            float[] firstEmbedding = CreateEmbedding(0.1f);
            float[] secondEmbedding = CreateEmbedding(0.2f);
            float[] thirdEmbedding = CreateEmbedding(0.3f);
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { firstEmbedding, secondEmbedding, thirdEmbedding });
            EmbeddingGenerationJob job = CreateJob();

            // Act
            await job.ExecuteAsync(CreateJobInfo(), CancellationToken.None);

            // Assert
            Assert.Same(firstEmbedding, firstChunk.Embedding);
            Assert.Same(secondEmbedding, secondChunk.Embedding);
            Assert.Same(thirdEmbedding, thirdChunk.Embedding);
            await _embeddingClient.Received(1).EmbedAsync(
                Arg.Is<IReadOnlyList<string>>(inputs => inputs.SequenceEqual(new[] { "first chunk", "second chunk", "third chunk" })),
                false,
                CancellationToken.None);
            Assert.Equal(2, _vectorSearchParamStore.ReplacedResources.Count);
            Assert.Same(firstResource, _vectorSearchParamStore.ReplacedResources[0]);
            Assert.Same(secondResource, _vectorSearchParamStore.ReplacedResources[1]);
        }

        [Fact]
        public async Task GivenEmbeddingResultCountMismatch_WhenExecuted_ThenDoesNotReplaceResources()
        {
            // Arrange
            ResourceWrapper resource = CreateResource();
            ConfigureResource(resource, "new text", 0);
            ConfigureStoredState();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(Array.Empty<float[]>());
            EmbeddingGenerationJob job = CreateJob();

            // Act
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => job.ExecuteAsync(CreateJobInfo(), CancellationToken.None));

            // Assert
            Assert.Equal(0, _vectorSearchParamStore.ReplaceCallCount);
        }

        [Fact]
        public async Task GivenEmbeddingWithWrongDimension_WhenExecuted_ThenDoesNotReplaceResources()
        {
            // Arrange
            ResourceWrapper resource = CreateResource();
            VectorSearchValue value = ConfigureResource(resource, "new text", 0);
            ConfigureStoredState();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { new float[1535] });
            EmbeddingGenerationJob job = CreateJob();

            // Act
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => job.ExecuteAsync(CreateJobInfo(), CancellationToken.None));

            // Assert
            Assert.Contains("1536", exception.Message, StringComparison.Ordinal);
            Assert.Contains("1535", exception.Message, StringComparison.Ordinal);
            Assert.Null(value.Embedding);
            Assert.Equal(0, _vectorSearchParamStore.ReplaceCallCount);
        }

        [Fact]
        public async Task GivenCancellationDuringResourceProcessing_WhenExecuted_ThenPropagatesOperationCanceledException()
        {
            // Arrange
            using var cancellationTokenSource = new CancellationTokenSource();
            ResourceWrapper firstResource = CreateResource(ResourceSurrogateId);
            ResourceWrapper secondResource = CreateResource(ResourceSurrogateId + 1);
            ConfigureSearchResults(firstResource, secondResource);
            ResourceElement firstResourceElement = _resourceDeserializer.Deserialize(firstResource);
            _searchIndexer.Extract(firstResourceElement).Returns(_ =>
            {
                cancellationTokenSource.Cancel();
                return Array.Empty<SearchIndexEntry>();
            });
            ResourceElement secondResourceElement = _resourceDeserializer.Deserialize(secondResource);
            _searchIndexer.Extract(secondResourceElement).Returns(Array.Empty<SearchIndexEntry>());
            EmbeddingGenerationJob job = CreateJob();

            // Act
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => job.ExecuteAsync(CreateJobInfo(), cancellationTokenSource.Token));

            // Assert
            _searchIndexer.DidNotReceive().Extract(secondResourceElement);
            Assert.Equal(0, _vectorSearchParamStore.GetStateCallCount);
        }

        [Fact]
        public void GivenSubstitutedDependencies_WhenConstructed_ThenDoesNotAccessSqlOrCapabilityState()
        {
            // Arrange
            var vectorSearchParamStore = new TestVectorSearchParamStore();
            IVectorSearchCapabilityProvider capabilityProvider = Substitute.For<IVectorSearchCapabilityProvider>();

            // Act
            _ = CreateJob(vectorSearchParamStore, capabilityProvider);

            // Assert
            Assert.Equal(0, vectorSearchParamStore.GetStateCallCount);
            Assert.Equal(0, vectorSearchParamStore.ReplaceCallCount);
            Assert.Empty(capabilityProvider.ReceivedCalls());
        }

        [Fact]
        public void EmbeddingGenerationJob_HasExpectedJobTypeId()
        {
            // Arrange
            JobTypeIdAttribute attribute = Assert.Single(
                typeof(EmbeddingGenerationJob).GetCustomAttributes(typeof(JobTypeIdAttribute), inherit: false).Cast<JobTypeIdAttribute>());

            // Act
            int jobTypeId = attribute.JobTypeId;

            // Assert
            Assert.Equal(11, jobTypeId);
        }

        [Fact]
        public void EmbeddingGenerationJob_IsDiscoveredBySqlServerJobAssemblyScan()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.TypesInSameAssemblyAs<EmbeddingGenerationJob>()
                .AssignableTo<IJob>()
                .Transient()
                .AsSelf();

            // Assert
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(EmbeddingGenerationJob));
        }

        private EmbeddingGenerationJob CreateJob(
            IVectorSearchParamStore vectorSearchParamStore = null,
            IVectorSearchCapabilityProvider capabilityProvider = null)
        {
            return new EmbeddingGenerationJob(
                () => _searchServiceScope,
                _searchIndexer,
                _resourceDeserializer,
                vectorSearchParamStore ?? _vectorSearchParamStore,
                _embeddingClient,
                capabilityProvider ?? _capabilityProvider,
                _model,
                _contextAccessor,
                _loggerFactory,
                _metricHandler);
        }

        private ResourceWrapper CreateResource(long resourceSurrogateId = ResourceSurrogateId)
        {
            var resource = new ResourceWrapper(
                Guid.NewGuid().ToString(),
                "1",
                "Patient",
                rawResource: null,
                request: null,
                DateTimeOffset.UtcNow,
                deleted: false,
                searchIndices: null,
                compartmentIndices: null,
                lastModifiedClaims: null,
                resourceSurrogateId: resourceSurrogateId);
            var resourceElement = new ResourceElement(Substitute.For<ITypedElement>());
            _resourceDeserializer.Deserialize(resource).Returns(resourceElement);
            ConfigureSearchResults(resource);
            return resource;
        }

        private void ConfigureSearchResults(params ResourceWrapper[] resources)
        {
            _searchService.SearchBySurrogateIdRange("Patient", 10, 20, Arg.Any<CancellationToken>())
                .Returns(CreateSearchResult(resources));
        }

        private VectorSearchValue ConfigureResource(ResourceWrapper resource, string sourceText, int chunkOrdinal)
        {
            var value = new VectorSearchValue(sourceText, chunkOrdinal);
            ConfigureResource(resource, value);
            return value;
        }

        private void ConfigureResource(ResourceWrapper resource, params VectorSearchValue[] values)
        {
            ResourceElement resourceElement = _resourceDeserializer.Deserialize(resource);
            IReadOnlyCollection<SearchIndexEntry> searchIndices = values
                .Select(value => new SearchIndexEntry(
                    new SearchParameterInfo("vector", "vector", SearchParamType.Special, SearchParameterUri),
                    value))
                .ToList();
            _searchIndexer.Extract(resourceElement).Returns(searchIndices);
        }

        private void ConfigureStoredState(ResourceWrapper resource = null, params VectorSearchParamState[] states)
        {
            IReadOnlyDictionary<long, IReadOnlyList<VectorSearchParamState>> state =
                resource == null || states.Length == 0
                    ? new Dictionary<long, IReadOnlyList<VectorSearchParamState>>()
                    : new Dictionary<long, IReadOnlyList<VectorSearchParamState>>
                    {
                        [resource.ResourceSurrogateId] = states,
                    };
            _vectorSearchParamStore.State = state;
        }

        private static float[] CreateEmbedding(float value = 0.1f)
        {
            return Enumerable.Repeat(value, VectorSearchCapabilityProvider.SchemaEmbeddingDimensions).ToArray();
        }

        private static SearchResult CreateSearchResult(params ResourceWrapper[] resources)
        {
            return new SearchResult(
                resources.Select(resource => new SearchResultEntry(resource)),
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: Array.Empty<Tuple<string, string>>());
        }

        private static JobInfo CreateJobInfo()
        {
            return new JobInfo
            {
                Definition = JsonConvert.SerializeObject(
                    new EmbeddingGenerationJobDefinition(
                        JobType.EmbeddingGenerationProcessing,
                        ResourceTypeId,
                        startSurrogateId: 10,
                        endSurrogateId: 20)),
            };
        }

        private sealed class EmbeddingGenerationJobResult
        {
            public int ResourcesProcessed { get; set; }

            public int ResourcesSkipped { get; set; }

            public int ResourcesEmbedded { get; set; }

            public int RowsWritten { get; set; }
        }

        private sealed class TestVectorSearchParamStore : IVectorSearchParamStore
        {
            public IReadOnlyDictionary<long, IReadOnlyList<VectorSearchParamState>> State { get; set; } =
                new Dictionary<long, IReadOnlyList<VectorSearchParamState>>();

            public int GetStateCallCount { get; private set; }

            public int ReplaceCallCount { get; private set; }

            public IReadOnlyList<ResourceWrapper> ReplacedResources { get; private set; } = Array.Empty<ResourceWrapper>();

            public Task<IReadOnlyDictionary<long, IReadOnlyList<VectorSearchParamState>>> GetStateAsync(
                short resourceTypeId,
                long startSurrogateId,
                long endSurrogateId,
                CancellationToken cancellationToken)
            {
                GetStateCallCount++;
                return Task.FromResult(State);
            }

            public Task ReplaceAsync(
                short resourceTypeId,
                IReadOnlyList<ResourceWrapper> resources,
                CancellationToken cancellationToken)
            {
                ReplaceCallCount++;
                ReplacedResources = resources;
                return Task.CompletedTask;
            }
        }
    }
}
