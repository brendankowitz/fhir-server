// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using Medino;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Storage.TvpRowGeneration;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.SqlServer.Features.Client;
using Microsoft.Health.SqlServer.Features.Schema;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Storage.TvpRowGeneration
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class VectorSearchParamListRowGeneratorTests
    {
        private readonly SqlServerFhirModel _model;
        private readonly SearchParameterToSearchValueTypeMap _searchParameterTypeMap = new SearchParameterToSearchValueTypeMap();

        public VectorSearchParamListRowGeneratorTests()
        {
            _model = new SqlServerFhirModel(
                new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max),
                Substitute.For<ISearchParameterDefinitionManager>(),
                () => null,
                Options.Create(new SecurityConfiguration()),
                Substitute.For<IScopeProvider<SqlConnectionWrapperFactory>>(),
                Substitute.For<IMediator>(),
                Substitute.For<ISqlRetryService>(),
                NullLogger<SqlServerFhirModel>.Instance);
        }

        [Fact]
        public void GivenAvailableVectorSearchAndEmbedding_WhenTryGenerateRow_ThenReturnsRowWithKeyAndModelFields()
        {
            // Arrange
            var capabilityProvider = CreateAvailableCapabilityProvider(42);
            var generator = CreateGenerator(capabilityProvider);
            VectorSearchValue searchValue = CreateSearchValue("source text", 7, new[] { 0.5f, -0.25f });

            // Act
            bool generated = generator.TryGenerateRow(3, 123456789, 9, searchValue, new HashSet<VectorSearchParamListRow>(), out VectorSearchParamListRow row);

            // Assert
            Assert.True(generated);
            Assert.Equal(3, row.ResourceTypeId);
            Assert.Equal(123456789, row.ResourceSurrogateId);
            Assert.Equal(9, row.SearchParamId);
            Assert.Equal(7, row.ChunkOrdinal);
            Assert.Equal(42, row.EmbeddingModelId);
        }

        [Fact]
        public void GivenEmbedding_WhenTryGenerateRow_ThenSerializesEmbeddingAsCompactJson()
        {
            // Arrange
            var generator = CreateGenerator(CreateAvailableCapabilityProvider(1));
            VectorSearchValue searchValue = CreateSearchValue("source text", 0, new[] { 0.5f, -0.25f });

            // Act
            bool generated = generator.TryGenerateRow(1, 2, 3, searchValue, new HashSet<VectorSearchParamListRow>(), out VectorSearchParamListRow row);

            // Assert
            Assert.True(generated);
            Assert.Equal("[0.5,-0.25]", row.Embedding);
        }

        [Fact]
        public void GivenEmbedding_WhenTryGenerateRow_ThenSourceTextHashRoundTrips()
        {
            // Arrange
            var generator = CreateGenerator(CreateAvailableCapabilityProvider(1));
            VectorSearchValue searchValue = CreateSearchValue("source text", 0, new[] { 0.5f });

            // Act
            bool generated = generator.TryGenerateRow(1, 2, 3, searchValue, new HashSet<VectorSearchParamListRow>(), out VectorSearchParamListRow row);

            // Assert
            Assert.True(generated);
            using var reader = new BinaryReader(row.SourceTextHash);
            Assert.Equal(searchValue.SourceTextHash, reader.ReadBytes(searchValue.SourceTextHash.Length));
        }

        [Fact]
        public void GivenMissingEmbedding_WhenTryGenerateRow_ThenDoesNotGenerateRow()
        {
            // Arrange
            var generator = CreateGenerator(CreateAvailableCapabilityProvider(1));
            var searchValue = new VectorSearchValue("source text", 0);

            // Act
            bool generated = generator.TryGenerateRow(1, 2, 3, searchValue, new HashSet<VectorSearchParamListRow>(), out VectorSearchParamListRow row);

            // Assert
            Assert.False(generated);
            Assert.Equal(default, row);
        }

        [Fact]
        public void GivenUnavailableVectorSearch_WhenTryGenerateRow_ThenDoesNotReadEmbeddingModelId()
        {
            // Arrange
            var capabilityProvider = Substitute.For<IVectorSearchCapabilityProvider>();
            capabilityProvider.IsVectorSearchAvailable.Returns(false);
            var generator = CreateGenerator(capabilityProvider);
            VectorSearchValue searchValue = CreateSearchValue("source text", 0, new[] { 0.5f });

            // Act
            bool generated = generator.TryGenerateRow(1, 2, 3, searchValue, new HashSet<VectorSearchParamListRow>(), out VectorSearchParamListRow row);

            // Assert
            Assert.False(generated);
            Assert.Equal(default, row);
            _ = capabilityProvider.DidNotReceive().EmbeddingModelId;
        }

        [Fact]
        public void GivenDuplicateKeyAndIdenticalValue_WhenTryGenerateRow_ThenDoesNotGenerateSecondRow()
        {
            // Arrange
            var generator = CreateGenerator(CreateAvailableCapabilityProvider(1));
            VectorSearchValue searchValue = CreateSearchValue("source text", 0, new[] { 0.5f });
            var results = new HashSet<VectorSearchParamListRow>();

            // Act
            bool firstGenerated = generator.TryGenerateRow(1, 2, 3, searchValue, results, out _);
            bool secondGenerated = generator.TryGenerateRow(1, 2, 3, searchValue, results, out VectorSearchParamListRow row);

            // Assert
            Assert.True(firstGenerated);
            Assert.False(secondGenerated);
            Assert.Equal(default, row);
        }

        [Fact]
        public void GivenDuplicateKeyAndDifferentEmbeddingAndHash_WhenTryGenerateRow_ThenDoesNotGenerateSecondRow()
        {
            // Arrange
            var generator = CreateGenerator(CreateAvailableCapabilityProvider(1));
            VectorSearchValue firstSearchValue = CreateSearchValue("first source text", 0, new[] { 0.5f });
            VectorSearchValue secondSearchValue = CreateSearchValue("second source text", 0, new[] { -0.25f });
            var results = new HashSet<VectorSearchParamListRow>();

            // Act
            bool firstGenerated = generator.TryGenerateRow(1, 2, 3, firstSearchValue, results, out _);
            bool secondGenerated = generator.TryGenerateRow(1, 2, 3, secondSearchValue, results, out VectorSearchParamListRow row);

            // Assert
            Assert.NotEqual(firstSearchValue.SourceTextHash, secondSearchValue.SourceTextHash);
            Assert.True(firstGenerated);
            Assert.False(secondGenerated);
            Assert.Equal(default, row);
        }

        [Fact]
        public void GivenDifferentChunkOrdinals_WhenTryGenerateRow_ThenGeneratesBothRows()
        {
            // Arrange
            var generator = CreateGenerator(CreateAvailableCapabilityProvider(1));
            VectorSearchValue firstSearchValue = CreateSearchValue("source text", 0, new[] { 0.5f });
            VectorSearchValue secondSearchValue = CreateSearchValue("source text", 1, new[] { 0.5f });
            var results = new HashSet<VectorSearchParamListRow>();

            // Act
            bool firstGenerated = generator.TryGenerateRow(1, 2, 3, firstSearchValue, results, out _);
            bool secondGenerated = generator.TryGenerateRow(1, 2, 3, secondSearchValue, results, out _);

            // Assert
            Assert.True(firstGenerated);
            Assert.True(secondGenerated);
        }

        private VectorSearchParamListRowGenerator CreateGenerator(IVectorSearchCapabilityProvider capabilityProvider)
        {
            return new VectorSearchParamListRowGenerator(_model, _searchParameterTypeMap, capabilityProvider);
        }

        private static IVectorSearchCapabilityProvider CreateAvailableCapabilityProvider(short embeddingModelId)
        {
            var capabilityProvider = Substitute.For<IVectorSearchCapabilityProvider>();
            capabilityProvider.IsVectorSearchAvailable.Returns(true);
            capabilityProvider.EmbeddingModelId.Returns(embeddingModelId);
            return capabilityProvider;
        }

        private static VectorSearchValue CreateSearchValue(string sourceText, int chunkOrdinal, float[] embedding)
        {
            var searchValue = new VectorSearchValue(sourceText, chunkOrdinal);
            searchValue.SetEmbedding(embedding);
            return searchValue;
        }
    }
}
