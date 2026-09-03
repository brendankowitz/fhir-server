// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.EmbeddingGeneration;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Watchdogs;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Watchdogs
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Operations)]
    public class VectorizationWatchdogTests
    {
        private const long Watermark = 10;
        private readonly IVectorSearchCapabilityProvider _capabilityProvider;
        private readonly ISearchParameterDefinitionManager _searchParameterDefinitionManager;
        private readonly ISqlServerFhirModel _model;

        public VectorizationWatchdogTests()
        {
            _capabilityProvider = Substitute.For<IVectorSearchCapabilityProvider>();
            _capabilityProvider.IsVectorSearchAvailable.Returns(true);
            _searchParameterDefinitionManager = Substitute.For<ISearchParameterDefinitionManager>();
            _model = Substitute.For<ISqlServerFhirModel>();
        }

        [Fact]
        public async Task GivenNoTransactions_WhenWatchdogRuns_ThenNothingIsEnqueuedOrAdvanced()
        {
            // Arrange
            ConfigureResourceType("Patient", 1);
            TestVectorizationWatchdog watchdog = CreateWatchdog(
                new[] { CreateSearchParameter("patient-vector", true, "Patient") },
                Array.Empty<(long, DateTime?, DateTime?)>());

            // Act
            await watchdog.RunWorkForTestingAsync(CancellationToken.None);

            // Assert
            Assert.Empty(watchdog.EnqueueCalls);
            Assert.Empty(watchdog.AdvancedWatermarks);
        }

        [Fact]
        public async Task GivenTransactionsAndVectorResourceTypes_WhenWatchdogRuns_ThenCrossProductJobsUseEmbeddingJobType()
        {
            // Arrange
            SearchParameterInfo patientParameter = CreateSearchParameter("patient-vector", true, "Patient");
            SearchParameterInfo observationParameter = CreateSearchParameter("observation-vector", true, "Observation");
            ConfigureResourceType("Patient", 1);
            ConfigureResourceType("Observation", 2);
            TestVectorizationWatchdog watchdog = CreateWatchdog(
                new[] { patientParameter, observationParameter },
                Transactions(20, 30, 40),
                jobBatchSize: 2);

            // Act
            await watchdog.RunWorkForTestingAsync(CancellationToken.None);

            // Assert
            string[] serializedDefinitions = Assert.Single(watchdog.EnqueueCalls);
            EmbeddingGenerationJobDefinition[] definitions = Deserialize(serializedDefinitions);
            Assert.Equal(4, definitions.Length);
            Assert.All(definitions, definition => Assert.Equal((int)JobType.EmbeddingGenerationProcessing, definition.TypeId));
            Assert.Equal(new short[] { 1, 2 }, definitions.Select(definition => definition.ResourceTypeId).Distinct().OrderBy(id => id));
            Assert.Equal(40, Assert.Single(watchdog.AdvancedWatermarks));
        }

        [Fact]
        public async Task GivenMultipleTransactionChunks_WhenWatchdogRuns_ThenRangesAreContiguousAndCoverMaximumTransaction()
        {
            // Arrange
            ConfigureResourceType("Patient", 1);
            TestVectorizationWatchdog watchdog = CreateWatchdogWithBatches(
                new[] { CreateSearchParameter("patient-vector", true, "Patient") },
                2,
                Transactions(30, 20),
                Transactions(40));

            // Act
            await watchdog.RunWorkForTestingAsync(CancellationToken.None);

            // Assert
            Assert.Equal(2, watchdog.EnqueueCalls.Count);
            EmbeddingGenerationJobDefinition[] definitions = watchdog.EnqueueCalls
                .SelectMany(Deserialize)
                .ToArray();
            Assert.Collection(
                definitions,
                first =>
                {
                    Assert.Equal(Watermark, first.StartSurrogateId);
                    Assert.Equal(30, first.EndSurrogateId);
                },
                second =>
                {
                    Assert.Equal(definitions[0].EndSurrogateId, second.StartSurrogateId);
                    Assert.Equal(40, second.EndSurrogateId);
                });
            Assert.Equal(40, definitions[^1].EndSurrogateId);
            Assert.Equal(new long[] { 30, 40 }, watchdog.AdvancedWatermarks);
        }

        [Fact]
        public async Task GivenFullTransactionBatchThenEmptyBatch_WhenWatchdogRuns_ThenItDrainsUntilEmpty()
        {
            // Arrange
            ConfigureResourceType("Patient", 1);
            long[] transactionIds = Enumerable.Range(1, 10000).Select(offset => Watermark + offset).ToArray();
            TestVectorizationWatchdog watchdog = CreateWatchdog(
                new[] { CreateSearchParameter("patient-vector", true, "Patient") },
                Transactions(transactionIds),
                jobBatchSize: 10000);

            // Act
            await watchdog.RunWorkForTestingAsync(CancellationToken.None);

            // Assert
            Assert.Equal(2, watchdog.TransactionFetchCount);
            Assert.Single(watchdog.EnqueueCalls);
            Assert.Equal(transactionIds[^1], Assert.Single(watchdog.AdvancedWatermarks));
        }

        [Fact]
        public async Task GivenSearchParameterWithoutVectorConfiguration_WhenWatchdogRuns_ThenItsResourceTypeProducesNoDefinition()
        {
            // Arrange
            ConfigureResourceType("Patient", 1);
            ConfigureResourceType("Observation", 2);
            TestVectorizationWatchdog watchdog = CreateWatchdog(
                new[]
                {
                    CreateSearchParameter("patient-vector", true, "Patient"),
                    CreateSearchParameter("observation-non-vector", false, "Observation"),
                },
                Transactions(20));

            // Act
            await watchdog.RunWorkForTestingAsync(CancellationToken.None);

            // Assert
            EmbeddingGenerationJobDefinition definition = Assert.Single(Deserialize(Assert.Single(watchdog.EnqueueCalls)));
            Assert.Equal((short)1, definition.ResourceTypeId);
        }

        [Fact]
        public async Task GivenVectorSearchUnavailable_WhenWatchdogRuns_ThenNothingIsReadEnqueuedOrAdvanced()
        {
            // Arrange
            _capabilityProvider.IsVectorSearchAvailable.Returns(false);
            TestVectorizationWatchdog watchdog = CreateWatchdog(
                new[] { CreateSearchParameter("patient-vector", true, "Patient") },
                Transactions(20));

            // Act
            await watchdog.RunWorkForTestingAsync(CancellationToken.None);

            // Assert
            await _capabilityProvider.Received(1).EnsureInitializedAsync(Arg.Any<CancellationToken>());
            Assert.Equal(0, watchdog.VisibilityReadCount);
            Assert.Empty(watchdog.EnqueueCalls);
            Assert.Empty(watchdog.AdvancedWatermarks);
        }

        [Fact]
        public async Task GivenEnqueueFailure_WhenWatchdogRuns_ThenWatermarkIsNotAdvanced()
        {
            // Arrange
            ConfigureResourceType("Patient", 1);
            TestVectorizationWatchdog watchdog = CreateWatchdog(
                new[] { CreateSearchParameter("patient-vector", true, "Patient") },
                Transactions(20));
            watchdog.EnqueueException = new InvalidOperationException("enqueue failed");

            // Act
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => watchdog.RunWorkForTestingAsync(CancellationToken.None));

            // Assert
            Assert.Equal("enqueue failed", exception.Message);
            Assert.Single(watchdog.EnqueueCalls);
            Assert.Empty(watchdog.AdvancedWatermarks);
        }

        private TestVectorizationWatchdog CreateWatchdog(
            IReadOnlyList<SearchParameterInfo> searchParameters,
            IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)> transactions,
            int jobBatchSize = 500)
        {
            _searchParameterDefinitionManager.AllSearchParameters.Returns(searchParameters);
            return new TestVectorizationWatchdog(
                _capabilityProvider,
                _searchParameterDefinitionManager,
                _model,
                Options.Create(new VectorSearchConfiguration { Enabled = true, JobBatchSize = jobBatchSize }),
                new[] { transactions });
        }

        private TestVectorizationWatchdog CreateWatchdogWithBatches(
            IReadOnlyList<SearchParameterInfo> searchParameters,
            int jobBatchSize,
            params IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)>[] transactionBatches)
        {
            _searchParameterDefinitionManager.AllSearchParameters.Returns(searchParameters);
            return new TestVectorizationWatchdog(
                _capabilityProvider,
                _searchParameterDefinitionManager,
                _model,
                Options.Create(new VectorSearchConfiguration { Enabled = true, JobBatchSize = jobBatchSize }),
                transactionBatches);
        }

        private void ConfigureResourceType(string resourceTypeName, short resourceTypeId)
        {
            _model.TryGetResourceTypeId(resourceTypeName, out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = resourceTypeId;
                    return true;
                });
        }

        private static SearchParameterInfo CreateSearchParameter(string name, bool isVector, params string[] resourceTypes)
        {
            return new SearchParameterInfo(name, name, SearchParamType.String, baseResourceTypes: resourceTypes)
            {
                VectorConfig = isVector ? new VectorSearchParameterConfig() : null,
            };
        }

        private static IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)> Transactions(params long[] ids)
        {
            return ids.Select(id => (id, (DateTime?)null, (DateTime?)null)).ToArray();
        }

        private static EmbeddingGenerationJobDefinition[] Deserialize(IEnumerable<string> definitions)
        {
            return definitions
                .Select(Newtonsoft.Json.JsonConvert.DeserializeObject<EmbeddingGenerationJobDefinition>)
                .ToArray();
        }

        private sealed class TestVectorizationWatchdog : VectorizationWatchdog
        {
            private readonly Queue<IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)>> _transactionBatches;
            private long _watermark = Watermark;

            public TestVectorizationWatchdog(
                IVectorSearchCapabilityProvider capabilityProvider,
                ISearchParameterDefinitionManager searchParameterDefinitionManager,
                ISqlServerFhirModel model,
                IOptions<VectorSearchConfiguration> configuration,
                IEnumerable<IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)>> transactionBatches)
                : base(
                    capabilityProvider,
                    searchParameterDefinitionManager,
                    model,
                    configuration,
                    NullLogger<VectorizationWatchdog>.Instance)
            {
                _transactionBatches = new Queue<IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)>>(transactionBatches);
            }

            public List<string[]> EnqueueCalls { get; } = new List<string[]>();

            public List<long> AdvancedWatermarks { get; } = new List<long>();

            public Exception EnqueueException { get; set; }

            public int VisibilityReadCount { get; private set; }

            public int TransactionFetchCount { get; private set; }

            internal override Task<long> GetTransactionVisibilityAsync(CancellationToken cancellationToken)
            {
                VisibilityReadCount++;
                return Task.FromResult(100000L);
            }

            internal override Task<long> GetLastProcessedTransactionIdAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(_watermark);
            }

            internal override Task<IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)>> GetTransactionsAsync(
                long startNotInclusiveTransactionId,
                long endInclusiveTransactionId,
                CancellationToken cancellationToken)
            {
                TransactionFetchCount++;
                IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)> transactions =
                    _transactionBatches.Count > 0
                        ? _transactionBatches.Dequeue()
                        : Array.Empty<(long, DateTime?, DateTime?)>();
                return Task.FromResult(transactions);
            }

            internal override Task EnqueueAsync(string[] definitions, CancellationToken cancellationToken)
            {
                EnqueueCalls.Add(definitions);
                return EnqueueException == null ? Task.CompletedTask : Task.FromException(EnqueueException);
            }

            internal override Task UpdateLastProcessedTransactionIdAsync(long lastTransactionId)
            {
                AdvancedWatermarks.Add(lastTransactionId);
                _watermark = lastTransactionId;
                return Task.CompletedTask;
            }
        }
    }
}
