// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Parameters;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Features.Search.VectorSearch;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Storage
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class VectorEmbeddingEnricherTests
    {
        private readonly IEmbeddingClient _embeddingClient = Substitute.For<IEmbeddingClient>();
        private readonly IVectorSearchCapabilityProvider _capabilityProvider = Substitute.For<IVectorSearchCapabilityProvider>();
        private readonly RequestContextAccessor<IFhirRequestContext> _requestContextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
        private readonly ILogger<VectorEmbeddingEnricher> _logger = Substitute.For<ILogger<VectorEmbeddingEnricher>>();
        private readonly IVectorSearchMetricHandler _metricHandler = Substitute.For<IVectorSearchMetricHandler>();
        private readonly Dictionary<string, StringValues> _requestHeaders = new();
        private readonly Dictionary<string, StringValues> _responseHeaders = new();

        public VectorEmbeddingEnricherTests()
        {
            _capabilityProvider.IsVectorSearchAvailable.Returns(true);
            _capabilityProvider.EnsureInitializedAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            _requestContextAccessor.RequestContext.RequestHeaders.Returns(_requestHeaders);
            _requestContextAccessor.RequestContext.ResponseHeaders.Returns(_responseHeaders);
        }

        [Fact]
        public async Task GivenNullResources_WhenEnriching_ThenReturnsFalseWithoutCallingClient()
        {
            // Arrange
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            bool result = await enricher.EnrichAsync(null, CancellationToken.None);

            // Assert
            Assert.False(result);
            await _capabilityProvider.DidNotReceiveWithAnyArgs().EnsureInitializedAsync(default);
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
        }

        [Fact]
        public async Task GivenEmptyResources_WhenEnriching_ThenReturnsFalseWithoutCallingClient()
        {
            // Arrange
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            bool result = await enricher.EnrichAsync(Array.Empty<ResourceWrapperOperation>(), CancellationToken.None);

            // Assert
            Assert.False(result);
            await _capabilityProvider.DidNotReceiveWithAnyArgs().EnsureInitializedAsync(default);
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
        }

        [Fact]
        public async Task GivenSynchronousModeAndPendingValue_WhenEnriching_ThenAppliesEmbeddingWithFullBudget()
        {
            // Arrange
            VectorSearchValue value = new("first", 0);
            float[] embedding = CreateEmbedding();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { embedding });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(value), CancellationToken.None);

            // Assert
            Assert.True(result);
            Assert.Same(embedding, value.Embedding);
            await _embeddingClient.Received(1).EmbedAsync(
                Arg.Is<IReadOnlyList<string>>(inputs => inputs.SequenceEqual(new[] { "first" })),
                false,
                CancellationToken.None);
            _metricHandler.Received(1).EmitEmbedding(
                Arg.Is<VectorEmbeddingMetricNotification>(notification =>
                    notification.Operation == VectorEmbeddingOperation.Write &&
                    notification.Outcome == VectorEmbeddingOutcome.Success &&
                    notification.ElapsedMilliseconds >= 0));
        }

        [Fact]
        public async Task GivenSeveralPendingValuesAcrossResources_WhenEnrichingSynchronously_ThenUsesOneOrderedBatch()
        {
            // Arrange
            VectorSearchValue first = new("first", 0);
            VectorSearchValue second = new("second", 0);
            VectorSearchValue third = new("third", 1);
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { CreateEmbedding(0.1f), CreateEmbedding(0.2f), CreateEmbedding(0.3f) });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);
            IReadOnlyList<ResourceWrapperOperation> resources =
            [
                CreateOperation(first),
                CreateOperation(second, third),
            ];

            // Act
            bool result = await enricher.EnrichAsync(resources, CancellationToken.None);

            // Assert
            Assert.True(result);
            await _embeddingClient.Received(1).EmbedAsync(
                Arg.Is<IReadOnlyList<string>>(inputs => inputs.SequenceEqual(new[] { "first", "second", "third" })),
                false,
                CancellationToken.None);
        }

        [Fact]
        public async Task GivenAsynchronousModeWithoutPreference_WhenEnriching_ThenReturnsFalseWithoutCallingClient()
        {
            // Arrange
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.False(result);
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
        }

        [Fact]
        public async Task GivenAsynchronousModeWithPreference_WhenEnriching_ThenUsesTightBudget()
        {
            // Arrange
            SetSynchronousPreference();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), true, Arg.Any<CancellationToken>())
                .Returns(new[] { CreateEmbedding() });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.True(result);
            await _embeddingClient.Received(1).EmbedAsync(
                Arg.Is<IReadOnlyList<string>>(inputs => inputs.SequenceEqual(new[] { "first" })),
                true,
                CancellationToken.None);
        }

        [Fact]
        public async Task GivenSynchronousPreference_WhenEmbeddingsAreApplied_ThenPreferenceAppliedHeaderIsSet()
        {
            // Arrange
            SetSynchronousPreference();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), true, Arg.Any<CancellationToken>())
                .Returns(new[] { CreateEmbedding() });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.True(result);
            Assert.Equal("embedding-persistence=synchronous", _responseHeaders[KnownHeaders.PreferenceApplied]);
        }

        [Fact]
        public async Task GivenSynchronousPreferenceAndDisabledFeature_WhenEnriching_ThenPreferenceAppliedHeaderIsNotSet()
        {
            // Arrange
            SetSynchronousPreference();
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous, enabled: false);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.False(result);
            Assert.DoesNotContain(KnownHeaders.PreferenceApplied, _responseHeaders);
        }

        [Fact]
        public async Task GivenSynchronousPreferenceAndClientFailure_WhenEnrichingAsynchronously_ThenReturnsFalseWithoutHeaderOrException()
        {
            // Arrange
            SetSynchronousPreference();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), true, Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<float[]>>>(_ => throw new EmbeddingUnavailableException());
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.False(result);
            Assert.DoesNotContain(KnownHeaders.PreferenceApplied, _responseHeaders);
        }

        [Fact]
        public async Task GivenNoPreferenceAndSynchronousMode_WhenEmbeddingsAreApplied_ThenPreferenceAppliedHeaderIsNotSet()
        {
            // Arrange
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { CreateEmbedding() });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.True(result);
            Assert.DoesNotContain(KnownHeaders.PreferenceApplied, _responseHeaders);
        }

        [Fact]
        public async Task GivenNoRequestContextAndSynchronousMode_WhenEmbeddingsAreApplied_ThenDoesNotThrow()
        {
            // Arrange
            _requestContextAccessor.RequestContext.Returns((IFhirRequestContext)null);
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { CreateEmbedding() });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task GivenFeatureIsDisabled_WhenEnriching_ThenReturnsFalseWithoutCallingClient()
        {
            // Arrange
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous, enabled: false);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.False(result);
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
        }

        [Fact]
        public async Task GivenVectorSearchIsUnavailable_WhenEnriching_ThenInitializesWithoutCallingClient()
        {
            // Arrange
            _capabilityProvider.IsVectorSearchAvailable.Returns(false);
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.False(result);
            await _capabilityProvider.Received(1).EnsureInitializedAsync(CancellationToken.None);
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
        }

        [Fact]
        public async Task GivenAllValuesAreAlreadyEmbedded_WhenEnriching_ThenReturnsTrueWithoutCallingClient()
        {
            // Arrange
            VectorSearchValue value = new("first", 0);
            value.SetEmbedding(new[] { 0.1f });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(value), CancellationToken.None);

            // Assert
            Assert.True(result);
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
        }

        [Fact]
        public async Task GivenClientIsUnavailable_WhenEnrichingSynchronously_ThenExceptionPropagates()
        {
            // Arrange
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<float[]>>>(_ => throw new EmbeddingUnavailableException());
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            Func<Task> act = () => enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            await Assert.ThrowsAsync<EmbeddingUnavailableException>(act);
            _metricHandler.Received(1).EmitEmbedding(
                Arg.Is<VectorEmbeddingMetricNotification>(notification =>
                    notification.Operation == VectorEmbeddingOperation.Write &&
                    notification.Outcome == VectorEmbeddingOutcome.Failure &&
                    notification.ElapsedMilliseconds >= 0));
        }

        [Fact]
        public async Task GivenClientIsUnavailable_WhenBestEffortEnriching_ThenReturnsFalseWithoutApplyingEmbedding()
        {
            // Arrange
            SetSynchronousPreference();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), true, Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<float[]>>>(_ => throw new EmbeddingUnavailableException());
            VectorSearchValue value = new("first", 0);
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(value), CancellationToken.None);

            // Assert
            Assert.False(result);
            Assert.Null(value.Embedding);
        }

        [Fact]
        public async Task GivenUnexpectedClientFailure_WhenBestEffortEnriching_ThenReturnsFalse()
        {
            // Arrange
            SetSynchronousPreference();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), true, Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<float[]>>>(_ => throw new InvalidOperationException());
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task GivenCallerCancellation_WhenBestEffortEnriching_ThenCancellationPropagates()
        {
            // Arrange
            SetSynchronousPreference();
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), true, Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<float[]>>>(_ => throw new OperationCanceledException(cancellationSource.Token));
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous);

            // Act
            Func<Task> act = () => enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), cancellationSource.Token);

            // Assert
            await Assert.ThrowsAsync<OperationCanceledException>(act);
        }

        [Fact]
        public async Task GivenTooFewEmbeddings_WhenEnrichingSynchronously_ThenThrowsWithoutApplyingAnyEmbedding()
        {
            // Arrange
            VectorSearchValue first = new("first", 0);
            VectorSearchValue second = new("second", 0);
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { new[] { 0.1f } });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            Func<Task> act = () => enricher.EnrichAsync(CreateOperations(first, second), CancellationToken.None);

            // Assert
            await Assert.ThrowsAsync<InvalidOperationException>(act);
            Assert.Null(first.Embedding);
            Assert.Null(second.Embedding);
        }

        [Fact]
        public async Task GivenTooFewEmbeddings_WhenBestEffortEnriching_ThenReturnsFalseWithoutApplyingAnyEmbedding()
        {
            // Arrange
            SetSynchronousPreference();
            VectorSearchValue first = new("first", 0);
            VectorSearchValue second = new("second", 0);
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), true, Arg.Any<CancellationToken>())
                .Returns(new[] { new[] { 0.1f } });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(first, second), CancellationToken.None);

            // Assert
            Assert.False(result);
            Assert.Null(first.Embedding);
            Assert.Null(second.Embedding);
        }

        [Fact]
        public async Task GivenEmbeddingWithWrongDimension_WhenEnrichingSynchronously_ThenThrowsWithoutApplyingEmbedding()
        {
            // Arrange
            VectorSearchValue value = new("first", 0);
            _embeddingClient.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), false, Arg.Any<CancellationToken>())
                .Returns(new[] { new float[1535] });
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Synchronous);

            // Act
            Func<Task> act = () => enricher.EnrichAsync(CreateOperations(value), CancellationToken.None);

            // Assert
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(act);
            Assert.Contains("1536", exception.Message, StringComparison.Ordinal);
            Assert.Contains("1535", exception.Message, StringComparison.Ordinal);
            Assert.Null(value.Embedding);
        }

        [Fact]
        public async Task GivenNoRequestContext_WhenEnrichingAsynchronously_ThenReturnsFalseWithoutCallingClient()
        {
            // Arrange
            _requestContextAccessor.RequestContext.Returns((IFhirRequestContext)null);
            VectorEmbeddingEnricher enricher = CreateEnricher(EmbeddingPersistenceMode.Asynchronous);

            // Act
            bool result = await enricher.EnrichAsync(CreateOperations(new VectorSearchValue("first", 0)), CancellationToken.None);

            // Assert
            Assert.False(result);
            await _embeddingClient.DidNotReceiveWithAnyArgs().EmbedAsync(default, default, default);
        }

        private VectorEmbeddingEnricher CreateEnricher(EmbeddingPersistenceMode mode, bool enabled = true)
        {
            return new VectorEmbeddingEnricher(
                _embeddingClient,
                _capabilityProvider,
                Options.Create(new VectorSearchConfiguration
                {
                    Enabled = enabled,
                    EmbeddingPersistenceMode = mode,
                }),
                _requestContextAccessor,
                _logger,
                _metricHandler);
        }

        private void SetSynchronousPreference()
        {
            _requestHeaders[KnownHeaders.Prefer] = "embedding-persistence=synchronous";
        }

        private static float[] CreateEmbedding(float value = 0.1f)
        {
            return Enumerable.Repeat(value, VectorSearchCapabilityProvider.SchemaEmbeddingDimensions).ToArray();
        }

        private static IReadOnlyList<ResourceWrapperOperation> CreateOperations(params VectorSearchValue[] values)
        {
            return new[] { CreateOperation(values) };
        }

        private static ResourceWrapperOperation CreateOperation(params VectorSearchValue[] values)
        {
            IReadOnlyCollection<SearchIndexEntry> searchIndices = values
                .Select(value => new SearchIndexEntry(new SearchParameterInfo("vector", "vector"), value))
                .ToList();
            var wrapper = new ResourceWrapper(
                Guid.NewGuid().ToString(),
                "1",
                "Patient",
                rawResource: null,
                request: null,
                DateTimeOffset.UtcNow,
                deleted: false,
                searchIndices,
                compartmentIndices: null,
                lastModifiedClaims: null);

            return new ResourceWrapperOperation(
                wrapper,
                allowCreate: true,
                keepHistory: false,
                weakETag: null,
                requireETagOnUpdate: false,
                keepVersion: false,
                bundleResourceContext: null);
        }
    }
}
