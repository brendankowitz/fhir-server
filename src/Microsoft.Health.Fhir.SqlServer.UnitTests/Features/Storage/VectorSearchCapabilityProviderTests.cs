// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.SqlServer.Features.Schema;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Storage
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.DataSourceValidation)]
    public class VectorSearchCapabilityProviderTests
    {
        private const short ExpectedEmbeddingModelId = 17;
        private readonly ISqlRetryService _sqlRetryService;
        private readonly ILogger<VectorSearchCapabilityProvider> _logger;

        public VectorSearchCapabilityProviderTests()
        {
            _sqlRetryService = Substitute.For<ISqlRetryService>();
            _logger = Substitute.For<ILogger<VectorSearchCapabilityProvider>>();
        }

        [Fact]
        public async Task GivenVectorSearchIsDisabled_WhenInitialized_ThenNoSqlIsExecuted()
        {
            // Arrange
            using VectorSearchCapabilityProvider provider = CreateProvider(enabled: false);

            // Act
            await provider.EnsureInitializedAsync(CancellationToken.None);

            // Assert
            Assert.False(provider.IsVectorSearchAvailable);
            Assert.Empty(_sqlRetryService.ReceivedCalls());
            _ = _sqlRetryService.DidNotReceiveWithAnyArgs().ExecuteReaderAsync<bool>(
                default,
                default,
                default,
                default,
                default,
                default);
        }

        [Fact]
        public async Task GivenSchemaIsBelowVectorSearchVersion_WhenInitialized_ThenNoSqlIsExecuted()
        {
            // Arrange
            using VectorSearchCapabilityProvider provider = CreateProvider(
                enabled: true,
                currentSchemaVersion: SchemaVersionConstants.VectorSearch - 1);

            // Act
            await provider.EnsureInitializedAsync(CancellationToken.None);

            // Assert
            Assert.False(provider.IsVectorSearchAvailable);
            Assert.Empty(_sqlRetryService.ReceivedCalls());
            _ = _sqlRetryService.DidNotReceiveWithAnyArgs().ExecuteReaderAsync<bool>(
                default,
                default,
                default,
                default,
                default,
                default);
        }

        [Fact]
        public async Task GivenSchemaIsUpgradedAfterInitialAttempt_WhenInitializedAgain_ThenVectorSearchBecomesAvailable()
        {
            // Arrange
            var schemaInformation = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max)
            {
                Current = SchemaVersionConstants.VectorSearch - 1,
            };
            using VectorSearchCapabilityProvider provider = CreateProvider(schemaInformation);

            // Act
            await provider.EnsureInitializedAsync(CancellationToken.None);

            // Assert
            Assert.False(provider.IsVectorSearchAvailable);
            Assert.Empty(_sqlRetryService.ReceivedCalls());

            // Arrange
            schemaInformation.Current = SchemaVersionConstants.VectorSearch;
            ConfigureSuccessfulInitialization(ExpectedEmbeddingModelId, persistedDimension: 1536);

            // Act
            await provider.EnsureInitializedAsync(CancellationToken.None);

            // Assert
            Assert.True(provider.IsVectorSearchAvailable);
            Assert.Equal(ExpectedEmbeddingModelId, provider.EmbeddingModelId);
        }

        [Fact]
        public async Task GivenVectorTypeIsUnavailable_WhenInitialized_ThenThrowsInvalidOperationException()
        {
            // Arrange
            ConfigureVectorTypeProbe(Array.Empty<bool>());
            using VectorSearchCapabilityProvider provider = CreateProvider();

            // Act
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.EnsureInitializedAsync(CancellationToken.None));

            // Assert
            Assert.Contains("VECTOR", exception.Message, StringComparison.Ordinal);
            Assert.False(provider.IsVectorSearchAvailable);
        }

        [Fact]
        public async Task GivenSupportedEngineAndMatchingModel_WhenInitialized_ThenCapabilityAndModelIdAreAvailable()
        {
            // Arrange
            ConfigureSuccessfulInitialization(ExpectedEmbeddingModelId, persistedDimension: 1536);
            using VectorSearchCapabilityProvider provider = CreateProvider();

            // Act
            await provider.EnsureInitializedAsync(CancellationToken.None);

            // Assert
            Assert.True(provider.IsVectorSearchAvailable);
            Assert.True(provider.SupportsApproximateVectorIndex);
            Assert.Equal(ExpectedEmbeddingModelId, provider.EmbeddingModelId);
        }

        [Fact]
        public async Task GivenPersistedModelHasDifferentDimension_WhenInitialized_ThenThrowsInvalidOperationException()
        {
            // Arrange
            ConfigureSuccessfulInitialization(ExpectedEmbeddingModelId, persistedDimension: 3072);
            using VectorSearchCapabilityProvider provider = CreateProvider();

            // Act
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.EnsureInitializedAsync(CancellationToken.None));

            // Assert
            Assert.Contains("1536", exception.Message, StringComparison.Ordinal);
            Assert.Contains("3072", exception.Message, StringComparison.Ordinal);
            Assert.False(provider.IsVectorSearchAvailable);
        }

        [Fact]
        public async Task GivenChunkConfiguration_WhenInitialized_ThenEmbeddingModelLookupConstrainsOnChunkConfiguration()
        {
            // Arrange
            ConfigureSuccessfulInitialization(ExpectedEmbeddingModelId, persistedDimension: 1536);
            using VectorSearchCapabilityProvider provider = CreateProvider();

            // Act
            await provider.EnsureInitializedAsync(CancellationToken.None);

            // Assert
            _ = _sqlRetryService.Received(1).ExecuteReaderAsync<(short EmbeddingModelId, int Dimension)>(
                Arg.Is<SqlCommand>(command =>
                    command.CommandText.Split("ChunkSizeTokens = @ChunkSizeTokens AND ChunkOverlapTokens = @ChunkOverlapTokens").Length == 3),
                Arg.Any<Func<SqlDataReader, (short EmbeddingModelId, int Dimension)>>(),
                Arg.Any<ILogger>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                isReadOnly: false);
        }

        [Fact]
        public async Task GivenProviderWasInitialized_WhenInitializedAgain_ThenSqlWorkIsNotRepeated()
        {
            // Arrange
            ConfigureSuccessfulInitialization(ExpectedEmbeddingModelId, persistedDimension: 1536);
            using VectorSearchCapabilityProvider provider = CreateProvider();

            // Act
            await provider.EnsureInitializedAsync(CancellationToken.None);
            await provider.EnsureInitializedAsync(CancellationToken.None);

            // Assert
            Assert.Equal(3, _sqlRetryService.ReceivedCalls().Count());
            _ = _sqlRetryService.Received(1).ExecuteReaderAsync<bool>(
                Arg.Any<SqlCommand>(),
                Arg.Any<Func<SqlDataReader, bool>>(),
                Arg.Any<ILogger>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>());
        }

        [Fact]
        public void GivenProviderWasNotInitialized_WhenEmbeddingModelIdIsAccessed_ThenThrowsInvalidOperationException()
        {
            // Arrange
            using VectorSearchCapabilityProvider provider = CreateProvider();

            // Act
            Action getEmbeddingModelId = () => _ = provider.EmbeddingModelId;

            // Assert
            Assert.Throws<InvalidOperationException>(getEmbeddingModelId);
        }

        private VectorSearchCapabilityProvider CreateProvider(
            bool enabled = true,
            int? currentSchemaVersion = SchemaVersionConstants.VectorSearch)
        {
            var schemaInformation = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max)
            {
                Current = currentSchemaVersion,
            };
            return CreateProvider(schemaInformation, enabled);
        }

        private VectorSearchCapabilityProvider CreateProvider(SchemaInformation schemaInformation, bool enabled = true)
        {
            var configuration = new VectorSearchConfiguration
            {
                Enabled = enabled,
                EmbeddingDeploymentName = "test-deployment",
                EmbeddingDimensions = 1536,
                EmbeddingModelVersion = "test-version",
            };

            return new VectorSearchCapabilityProvider(
                _sqlRetryService,
                schemaInformation,
                Options.Create(configuration),
                _logger);
        }

        private void ConfigureSuccessfulInitialization(short embeddingModelId, int persistedDimension)
        {
            ConfigureVectorTypeProbe(new[] { true });

            _sqlRetryService.ExecuteReaderAsync<int?>(
                    Arg.Is<SqlCommand>(command => command.CommandText.Contains("dm_db_vector_indexes", StringComparison.Ordinal)),
                    Arg.Any<Func<SqlDataReader, int?>>(),
                    Arg.Any<ILogger>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    isReadOnly: true)
                .Returns(Task.FromResult<IReadOnlyList<int?>>(new int?[] { 123 }));

            _sqlRetryService.ExecuteReaderAsync<(short EmbeddingModelId, int Dimension)>(
                    Arg.Is<SqlCommand>(command => command.CommandText.Contains("dbo.EmbeddingModel", StringComparison.Ordinal)),
                    Arg.Any<Func<SqlDataReader, (short EmbeddingModelId, int Dimension)>>(),
                    Arg.Any<ILogger>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    isReadOnly: false)
                .Returns(Task.FromResult<IReadOnlyList<(short EmbeddingModelId, int Dimension)>>(
                    new[] { (embeddingModelId, persistedDimension) }));
        }

        private void ConfigureVectorTypeProbe(IReadOnlyList<bool> results)
        {
            _sqlRetryService.ExecuteReaderAsync<bool>(
                    Arg.Is<SqlCommand>(command => command.CommandText.Contains("sys.types", StringComparison.Ordinal)),
                    Arg.Any<Func<SqlDataReader, bool>>(),
                    Arg.Any<ILogger>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    isReadOnly: true)
                .Returns(Task.FromResult(results));
        }
    }
}
