// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.Fhir.SqlServer.Features.Storage.TvpRowGeneration.Merge;
using Microsoft.Health.SqlServer.Features.Schema.Model;

namespace Microsoft.Health.Fhir.SqlServer.Features.Storage
{
    internal sealed class SqlVectorSearchParamStore : IVectorSearchParamStore
    {
        private const string ReadStateSql = @"
SELECT ResourceSurrogateId, SearchParamId, ChunkOrdinal, SourceTextHash, EmbeddingModelId
FROM dbo.VectorSearchParam
WHERE ResourceTypeId = @ResourceTypeId
  AND ResourceSurrogateId BETWEEN @StartSurrogateId AND @EndSurrogateId;";

        private readonly ISqlRetryService _sqlRetryService;
        private readonly ITableValuedParameterRowGenerator<IReadOnlyList<MergeResourceWrapper>, VectorSearchParamListRow> _rowGenerator;
        private readonly SqlServerFhirModel _model;
        private readonly ILogger<SqlVectorSearchParamStore> _logger;

        public SqlVectorSearchParamStore(
            ISqlRetryService sqlRetryService,
            ITableValuedParameterRowGenerator<IReadOnlyList<MergeResourceWrapper>, VectorSearchParamListRow> rowGenerator,
            SqlServerFhirModel model,
            ILogger<SqlVectorSearchParamStore> logger)
        {
            _sqlRetryService = EnsureArg.IsNotNull(sqlRetryService, nameof(sqlRetryService));
            _rowGenerator = EnsureArg.IsNotNull(rowGenerator, nameof(rowGenerator));
            _model = EnsureArg.IsNotNull(model, nameof(model));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        public async Task<IReadOnlyDictionary<long, IReadOnlyList<VectorSearchParamState>>> GetStateAsync(
            short resourceTypeId,
            long startSurrogateId,
            long endSurrogateId,
            CancellationToken cancellationToken)
        {
            using var command = new SqlCommand(ReadStateSql);
            command.Parameters.Add("@ResourceTypeId", SqlDbType.SmallInt).Value = resourceTypeId;
            command.Parameters.Add("@StartSurrogateId", SqlDbType.BigInt).Value = startSurrogateId;
            command.Parameters.Add("@EndSurrogateId", SqlDbType.BigInt).Value = endSurrogateId;

            IReadOnlyList<(long ResourceSurrogateId, short SearchParamId, short ChunkOrdinal, byte[] SourceTextHash, short EmbeddingModelId)> rows =
                await _sqlRetryService.ExecuteReaderAsync(
                command,
                reader => ReadState(reader),
                _logger,
                "Reading stored vector search parameter state.",
                cancellationToken,
                isReadOnly: true);

            return rows
                .GroupBy(row => row.ResourceSurrogateId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<VectorSearchParamState>)group
                        .Select(row => new VectorSearchParamState(
                            row.SearchParamId,
                            row.ChunkOrdinal,
                            row.SourceTextHash,
                            row.EmbeddingModelId))
                        .ToList());
        }

        public async Task ReplaceAsync(
            short resourceTypeId,
            IReadOnlyList<ResourceWrapper> resources,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resources, nameof(resources));

            if (resources.Count == 0)
            {
                return;
            }

            foreach (ResourceWrapper resource in resources)
            {
                if (_model.GetResourceTypeId(resource.ResourceTypeName) != resourceTypeId)
                {
                    throw new ArgumentException(
                        $"Resource type '{resource.ResourceTypeName}' does not match resource type id {resourceTypeId}.",
                        nameof(resources));
                }
            }

            IReadOnlyList<MergeResourceWrapper> mergeResources = resources
                .Select(resource => new MergeResourceWrapper(resource, keepHistory: false, hasVersionToCompare: false))
                .ToList();

            using var command = new SqlCommand("dbo.MergeVectorSearchParams")
            {
                CommandType = CommandType.StoredProcedure,
            };

            command.Parameters.Add("@ResourceTypeId", SqlDbType.SmallInt).Value = resourceTypeId;
            new BigintListTableValuedParameterDefinition("@ResourceSurrogateIds")
                .AddParameter(
                    command.Parameters,
                    resources.Select(resource => resource.ResourceSurrogateId).Distinct().Select(id => new BigintListRow(id)));
            new VectorSearchParamListTableValuedParameterDefinition("@VectorSearchParams")
                .AddParameter(command.Parameters, _rowGenerator.GenerateRows(mergeResources));

            await command.ExecuteNonQueryAsync(
                _sqlRetryService,
                _logger,
                cancellationToken,
                logMessage: "Replacing stored vector search parameters.");
        }

        internal static (long ResourceSurrogateId, short SearchParamId, short ChunkOrdinal, byte[] SourceTextHash, short EmbeddingModelId) ReadState(
            DbDataReader reader)
        {
            return (
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3),
                reader.GetInt16(4));
        }
    }
}
