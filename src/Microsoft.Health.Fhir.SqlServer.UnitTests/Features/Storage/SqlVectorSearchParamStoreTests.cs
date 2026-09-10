// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Medino;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Storage.TvpRowGeneration;
using Microsoft.Health.Fhir.SqlServer.Features.Storage.TvpRowGeneration.Merge;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.SqlServer.Features.Client;
using Microsoft.Health.SqlServer.Features.Schema;
using Microsoft.Health.SqlServer.Features.Schema.Model;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Storage
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.DataSourceValidation)]
    public class SqlVectorSearchParamStoreTests
    {
        private readonly ISqlRetryService _sqlRetryService = Substitute.For<ISqlRetryService>();
        private readonly RecordingRowGenerator _rowGenerator = new();
        private readonly ILogger<SqlVectorSearchParamStore> _logger = NullLogger<SqlVectorSearchParamStore>.Instance;
        private readonly SqlServerFhirModel _model = CreateModel();
        private SqlCommand _capturedCommand;

        [Fact]
        public async Task GivenResourceRange_WhenReadingState_ThenQueryUsesClusteredKeyPrefixWithoutSelectingVector()
        {
            // Arrange
            SqlCommand capturedCommand = null;
            _sqlRetryService.ExecuteReaderAsync<(long ResourceSurrogateId, short SearchParamId, short ChunkOrdinal, byte[] SourceTextHash, short EmbeddingModelId)>(
                    Arg.Do<SqlCommand>(command => capturedCommand = command),
                    Arg.Any<Func<SqlDataReader, (long ResourceSurrogateId, short SearchParamId, short ChunkOrdinal, byte[] SourceTextHash, short EmbeddingModelId)>>(),
                    Arg.Any<ILogger>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    isReadOnly: true)
                .Returns(Task.FromResult<IReadOnlyList<(long, short, short, byte[], short)>>(Array.Empty<(long, short, short, byte[], short)>()));
            SqlVectorSearchParamStore store = CreateStore();

            // Act
            await store.GetStateAsync(7, 100, 200, CancellationToken.None);

            // Assert
            Assert.NotNull(capturedCommand);
            Assert.Contains("ResourceTypeId = @ResourceTypeId", capturedCommand.CommandText, StringComparison.Ordinal);
            Assert.Contains("ResourceSurrogateId BETWEEN @StartSurrogateId AND @EndSurrogateId", capturedCommand.CommandText, StringComparison.Ordinal);
            string selectList = capturedCommand.CommandText[
                (capturedCommand.CommandText.IndexOf("SELECT", StringComparison.Ordinal) + "SELECT".Length)..
                capturedCommand.CommandText.IndexOf("FROM", StringComparison.Ordinal)];
            Assert.DoesNotContain(
                selectList.Split(',', StringSplitOptions.TrimEntries),
                column => string.Equals(column, "Embedding", StringComparison.OrdinalIgnoreCase));
            Assert.Equal((short)7, capturedCommand.Parameters["@ResourceTypeId"].Value);
            Assert.Equal(100L, capturedCommand.Parameters["@StartSurrogateId"].Value);
            Assert.Equal(200L, capturedCommand.Parameters["@EndSurrogateId"].Value);
        }

        [Fact]
        public async Task GivenResources_WhenWriting_ThenBindsVectorTvpAndCallsMergeProcedure()
        {
            // Arrange
            ResourceWrapper resource = CreateResourceWrapper(2);
            _rowGenerator.Rows = new[] { new VectorSearchParamListRow(1, 2, 3, 4, "[0.5]", 5, null, null) };
            CaptureNonQueryCommand();
            SqlVectorSearchParamStore store = CreateStore();

            // Act
            await store.ReplaceAsync(1, new[] { resource }, CancellationToken.None);

            // Assert
            Assert.NotNull(_capturedCommand);
            Assert.Equal(CommandType.StoredProcedure, _capturedCommand.CommandType);
            Assert.Equal("dbo.MergeVectorSearchParams", _capturedCommand.CommandText);
            Assert.Equal((short)1, _capturedCommand.Parameters["@ResourceTypeId"].Value);
            Assert.Equal(new[] { 2L }, ReadBigintTvp(_capturedCommand.Parameters["@ResourceSurrogateIds"]));
            SqlParameter vectorParameter = _capturedCommand.Parameters["@VectorSearchParams"];
            Assert.Equal(SqlDbType.Structured, vectorParameter.SqlDbType);
            Assert.Equal("dbo.VectorSearchParamList", vectorParameter.TypeName);
            Assert.NotNull(_rowGenerator.Resources);
            MergeResourceWrapper mergeResource = Assert.Single(_rowGenerator.Resources);
            Assert.Same(resource, mergeResource.ResourceWrapper);
            Assert.False(mergeResource.KeepHistory);
            Assert.False(mergeResource.HasVersionToCompare);
        }

        [Fact]
        public async Task GivenNoResources_WhenWriting_ThenDoesNotCallDatabaseOrGenerator()
        {
            // Arrange
            SqlVectorSearchParamStore store = CreateStore();

            // Act
            await store.ReplaceAsync(1, Array.Empty<ResourceWrapper>(), CancellationToken.None);

            // Assert
            Assert.Empty(_sqlRetryService.ReceivedCalls());
            Assert.Null(_rowGenerator.Resources);
        }

        [Fact]
        public async Task GivenResourceGeneratesNoVectorRows_WhenReplacing_ThenAffectedResourceTvpStillContainsResource()
        {
            // Arrange
            ResourceWrapper resource = CreateResourceWrapper(123);
            CaptureNonQueryCommand();
            SqlVectorSearchParamStore store = CreateStore();

            // Act
            await store.ReplaceAsync(1, new[] { resource }, CancellationToken.None);

            // Assert
            Assert.Empty(_rowGenerator.Rows);
            Assert.Equal(new[] { 123L }, ReadBigintTvp(_capturedCommand.Parameters["@ResourceSurrogateIds"]));
        }

        [Fact]
        public async Task GivenGeneratedRowsForOnlyOneResource_WhenReplacing_ThenAffectedResourceTvpContainsEveryInputResource()
        {
            // Arrange
            ResourceWrapper firstResource = CreateResourceWrapper(100);
            ResourceWrapper secondResource = CreateResourceWrapper(200);
            _rowGenerator.Rows = new[] { new VectorSearchParamListRow(1, 100, 3, 0, "[0.5]", 5, null, null) };
            CaptureNonQueryCommand();
            SqlVectorSearchParamStore store = CreateStore();

            // Act
            await store.ReplaceAsync(1, new[] { firstResource, secondResource }, CancellationToken.None);

            // Assert
            Assert.Equal(new[] { 100L, 200L }, ReadBigintTvp(_capturedCommand.Parameters["@ResourceSurrogateIds"]));
            Assert.Equal(2, _rowGenerator.Resources.Count);
        }

        [Fact]
        public async Task GivenResourceTypeDoesNotMatch_WhenReplacing_ThenThrowsBeforeCallingDatabase()
        {
            // Arrange
            ResourceWrapper resource = CreateResourceWrapper(100, "Observation");
            SqlVectorSearchParamStore store = CreateStore();

            // Act
            ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
                () => store.ReplaceAsync(1, new[] { resource }, CancellationToken.None));

            // Assert
            Assert.Contains("Observation", exception.Message, StringComparison.Ordinal);
            Assert.Empty(_sqlRetryService.ReceivedCalls());
            Assert.Null(_rowGenerator.Resources);
        }

        [Fact]
        public async Task GivenStoredRowsAcrossResources_WhenReadingState_ThenMaterializesGroupsAndPreservesFields()
        {
            // Arrange
            byte[] firstHash = { 1, 2, 3 };
            byte[] secondHash = { 4, 5, 6 };
            var table = new DataTable();
            table.Columns.Add("ResourceSurrogateId", typeof(long));
            table.Columns.Add("SearchParamId", typeof(short));
            table.Columns.Add("ChunkOrdinal", typeof(short));
            table.Columns.Add("SourceTextHash", typeof(byte[]));
            table.Columns.Add("EmbeddingModelId", typeof(short));
            table.Rows.Add(100L, (short)10, (short)0, firstHash, (short)20);
            table.Rows.Add(100L, (short)11, (short)1, secondHash, (short)21);
            table.Rows.Add(200L, (short)12, (short)2, DBNull.Value, (short)22);

            var materializedRows = new List<(long ResourceSurrogateId, short SearchParamId, short ChunkOrdinal, byte[] SourceTextHash, short EmbeddingModelId)>();
            using (DbDataReader reader = table.CreateDataReader())
            {
                while (reader.Read())
                {
                    materializedRows.Add(SqlVectorSearchParamStore.ReadState(reader));
                }
            }

            _sqlRetryService.ExecuteReaderAsync<(long ResourceSurrogateId, short SearchParamId, short ChunkOrdinal, byte[] SourceTextHash, short EmbeddingModelId)>(
                    Arg.Any<SqlCommand>(),
                    Arg.Any<Func<SqlDataReader, (long ResourceSurrogateId, short SearchParamId, short ChunkOrdinal, byte[] SourceTextHash, short EmbeddingModelId)>>(),
                    Arg.Any<ILogger>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    isReadOnly: true)
                .Returns(Task.FromResult<IReadOnlyList<(long, short, short, byte[], short)>>(materializedRows));
            SqlVectorSearchParamStore store = CreateStore();

            // Act
            IReadOnlyDictionary<long, IReadOnlyList<VectorSearchParamState>> result =
                await store.GetStateAsync(1, 100, 200, CancellationToken.None);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Collection(
                result[100],
                state =>
                {
                    Assert.Equal((short)10, state.SearchParamId);
                    Assert.Equal((short)0, state.ChunkOrdinal);
                    Assert.Equal(firstHash, state.SourceTextHash);
                    Assert.Equal((short)20, state.EmbeddingModelId);
                },
                state =>
                {
                    Assert.Equal((short)11, state.SearchParamId);
                    Assert.Equal((short)1, state.ChunkOrdinal);
                    Assert.Equal(secondHash, state.SourceTextHash);
                    Assert.Equal((short)21, state.EmbeddingModelId);
                });
            VectorSearchParamState secondResourceState = Assert.Single(result[200]);
            Assert.Equal((short)12, secondResourceState.SearchParamId);
            Assert.Equal((short)2, secondResourceState.ChunkOrdinal);
            Assert.Null(secondResourceState.SourceTextHash);
            Assert.Equal((short)22, secondResourceState.EmbeddingModelId);
        }

        private SqlVectorSearchParamStore CreateStore()
        {
            return new SqlVectorSearchParamStore(_sqlRetryService, _rowGenerator, _model, _logger);
        }

        private void CaptureNonQueryCommand()
        {
            _sqlRetryService.ExecuteSql(
                    Arg.Do<SqlCommand>(command => _capturedCommand = command),
                    Arg.Any<Func<SqlCommand, CancellationToken, Task>>(),
                    Arg.Any<ILogger>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>(),
                    Arg.Any<string>())
                .Returns(Task.CompletedTask);
        }

        private static IReadOnlyList<long> ReadBigintTvp(SqlParameter parameter)
        {
            Assert.Equal(SqlDbType.Structured, parameter.SqlDbType);
            Assert.Equal("dbo.BigintList", parameter.TypeName);
            return ((IEnumerable<SqlDataRecord>)parameter.Value).Select(record => record.GetInt64(0)).ToList();
        }

        private static ResourceWrapper CreateResourceWrapper(long resourceSurrogateId = 0, string resourceTypeName = "Patient")
        {
            return new ResourceWrapper(
                Guid.NewGuid().ToString(),
                "1",
                resourceTypeName,
                rawResource: null,
                request: null,
                DateTimeOffset.UtcNow,
                deleted: false,
                searchIndices: null,
                compartmentIndices: null,
                lastModifiedClaims: null,
                resourceSurrogateId: resourceSurrogateId);
        }

        private static SqlServerFhirModel CreateModel()
        {
            var schemaInformation = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max)
            {
                Current = SchemaVersionConstants.Max,
            };
            var model = new SqlServerFhirModel(
                schemaInformation,
                Substitute.For<ISearchParameterDefinitionManager>(),
                () => null,
                Options.Create(new SecurityConfiguration()),
                Substitute.For<IScopeProvider<SqlConnectionWrapperFactory>>(),
                Substitute.For<IMediator>(),
                Substitute.For<ISqlRetryService>(),
                NullLogger<SqlServerFhirModel>.Instance);

            typeof(SqlServerFhirModel)
                .GetField("_resourceTypeToId", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(
                    model,
                    new Dictionary<string, short>(StringComparer.Ordinal)
                    {
                        ["Patient"] = 1,
                        ["Observation"] = 2,
                    });
            typeof(SqlServerFhirModel)
                .GetField("_highestInitializedVersion", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(model, schemaInformation.Current);

            return model;
        }

        private sealed class RecordingRowGenerator : ITableValuedParameterRowGenerator<IReadOnlyList<MergeResourceWrapper>, VectorSearchParamListRow>
        {
            internal IReadOnlyList<MergeResourceWrapper> Resources { get; private set; }

            internal IEnumerable<VectorSearchParamListRow> Rows { get; set; } = Array.Empty<VectorSearchParamListRow>();

            public IEnumerable<VectorSearchParamListRow> GenerateRows(IReadOnlyList<MergeResourceWrapper> input)
            {
                Resources = input;
                return Rows;
            }
        }
    }
}
