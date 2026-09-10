// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.SqlServer.Features.Schema;

namespace Microsoft.Health.Fhir.SqlServer.Features.Storage
{
    /// <summary>
    /// Initializes and caches the SQL capabilities required by vector search.
    /// </summary>
    public sealed class VectorSearchCapabilityProvider : IVectorSearchCapabilityProvider, IDisposable
    {
        internal const int SchemaEmbeddingDimensions = 1536;

        private const string DistanceMetric = "cosine";
        private readonly ISqlRetryService _sqlRetryService;
        private readonly SchemaInformation _schemaInformation;
        private readonly VectorSearchConfiguration _configuration;
        private readonly ILogger<VectorSearchCapabilityProvider> _logger;
        private readonly SemaphoreSlim _initializationLock = new(1, 1);
        private bool _initialized;
        private int _disabledLogged;
        private int _schemaGateLogged;
        private int _approximateUnavailableLogged;
        private short _embeddingModelId;

        /// <summary>
        /// Initializes a new instance of the <see cref="VectorSearchCapabilityProvider"/> class.
        /// </summary>
        /// <param name="sqlRetryService">The SQL retry service.</param>
        /// <param name="schemaInformation">The current SQL schema information.</param>
        /// <param name="configuration">The vector search configuration.</param>
        /// <param name="logger">The logger.</param>
        public VectorSearchCapabilityProvider(
            ISqlRetryService sqlRetryService,
            SchemaInformation schemaInformation,
            IOptions<VectorSearchConfiguration> configuration,
            ILogger<VectorSearchCapabilityProvider> logger)
        {
            _sqlRetryService = EnsureArg.IsNotNull(sqlRetryService, nameof(sqlRetryService));
            _schemaInformation = EnsureArg.IsNotNull(schemaInformation, nameof(schemaInformation));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration)).Value;
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public bool IsVectorSearchAvailable { get; private set; }

        /// <inheritdoc />
        public bool SupportsApproximateVectorIndex { get; private set; }

        /// <inheritdoc />
        public short EmbeddingModelId
        {
            get
            {
                if (!Volatile.Read(ref _initialized))
                {
                    throw new InvalidOperationException("Vector search capabilities have not been initialized.");
                }

                return _embeddingModelId;
            }
        }

        /// <summary>
        /// Releases resources owned by the provider.
        /// </summary>
        public void Dispose()
        {
            _initializationLock.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _initialized))
            {
                return;
            }

            if (!_configuration.Enabled)
            {
                if (Interlocked.Exchange(ref _disabledLogged, 1) == 0)
                {
                    _logger.LogDebug("Vector search is disabled; capability initialization was skipped.");
                }

                return;
            }

            ValidateConfiguration();

            await _initializationLock.WaitAsync(cancellationToken);

            try
            {
                if (Volatile.Read(ref _initialized))
                {
                    return;
                }

                if (_schemaInformation.Current is null ||
                    _schemaInformation.Current < SchemaVersionConstants.VectorSearch)
                {
                    if (Interlocked.Exchange(ref _schemaGateLogged, 1) == 0)
                    {
                        _logger.LogInformation(
                            "Vector search is enabled but schema version {Current} is below the required {Required}; vector search will remain unavailable until the schema is upgraded.",
                            _schemaInformation.Current,
                            SchemaVersionConstants.VectorSearch);
                    }

                    return;
                }

                await EnsureVectorTypeSupportedAsync(cancellationToken);
                await ProbeApproximateVectorIndexAsync(cancellationToken);
                LogApproximateIndexFallbackIfNeeded();
                (short embeddingModelId, int dimension) = await UpsertEmbeddingModelAsync(cancellationToken);

                if (dimension != _configuration.EmbeddingDimensions)
                {
                    throw new InvalidOperationException(
                        $"The persisted embedding dimension {dimension} for model '{_configuration.EmbeddingDeploymentName}' version '{_configuration.EmbeddingModelVersion}' does not match Schema V{SchemaVersionConstants.VectorSearch}, which stores vectors at a fixed {SchemaEmbeddingDimensions} dimensions. Use a model identity and deployment that produce {SchemaEmbeddingDimensions}-dimension embeddings.");
                }

                _embeddingModelId = embeddingModelId;
                IsVectorSearchAvailable = true;

                // Creating the approximate (DiskANN) vector index is deliberately deferred to Phase 7:
                // a SQL vector index requires at least 100 non-NULL vectors and the table is empty at
                // startup, so the index must be built lazily once embeddings exist.
                Volatile.Write(ref _initialized, true);

                _logger.LogInformation(
                    "Vector search initialized with model {ModelName}, version {ModelVersion}, dimension {Dimension}, embedding model id {EmbeddingModelId}, and approximate indexing availability {SupportsApproximateVectorIndex}.",
                    _configuration.EmbeddingDeploymentName,
                    _configuration.EmbeddingModelVersion,
                    _configuration.EmbeddingDimensions,
                    _embeddingModelId,
                    SupportsApproximateVectorIndex);
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        private void ValidateConfiguration()
        {
            // The chunker estimates four characters per token. The 1024 ceiling keeps the estimated
            // 4096-character chunk below the embedding client's exact 8192-token truncation limit
            // even when text approaches one token per character.
            if (_configuration.ChunkSizeTokens < 1 || _configuration.ChunkSizeTokens > 1024)
            {
                throw new InvalidOperationException(
                    $"VectorSearch:ChunkSizeTokens must be between 1 and 1024 because chunk sizing is estimated while the embedding service truncates at an exact token limit, but was {_configuration.ChunkSizeTokens}.");
            }

            if (_configuration.ChunkOverlapTokens < 0 || _configuration.ChunkOverlapTokens >= _configuration.ChunkSizeTokens)
            {
                throw new InvalidOperationException(
                    $"VectorSearch:ChunkOverlapTokens must be between 0 and one less than VectorSearch:ChunkSizeTokens ({_configuration.ChunkSizeTokens}), but was {_configuration.ChunkOverlapTokens}.");
            }

            if (_configuration.MaxChunksPerSearchParameter < 1)
            {
                throw new InvalidOperationException(
                    $"VectorSearch:MaxChunksPerSearchParameter must be at least 1, but was {_configuration.MaxChunksPerSearchParameter}.");
            }

            if (_configuration.EmbeddingDimensions != SchemaEmbeddingDimensions)
            {
                throw new InvalidOperationException(
                    $"VectorSearch:EmbeddingDimensions must be {SchemaEmbeddingDimensions} because Schema V{SchemaVersionConstants.VectorSearch} stores vectors at a fixed {SchemaEmbeddingDimensions} dimensions, but was {_configuration.EmbeddingDimensions}.");
            }
        }

        private async Task EnsureVectorTypeSupportedAsync(CancellationToken cancellationToken)
        {
            using var command = new SqlCommand("SELECT 1 FROM sys.types WHERE name = 'vector' AND is_user_defined = 0");
            IReadOnlyList<bool> results = await _sqlRetryService.ExecuteReaderAsync(
                command,
                _ => true,
                _logger,
                "Probing SQL engine support for the VECTOR type.",
                cancellationToken,
                isReadOnly: true);

            if (results.Count == 0)
            {
                throw new InvalidOperationException(
                    "Vector search is enabled but this SQL engine does not support the VECTOR type. Vector search requires Azure SQL Database. Set VectorSearch:Enabled to false or migrate to a supported engine.");
            }
        }

        private async Task ProbeApproximateVectorIndexAsync(CancellationToken cancellationToken)
        {
            try
            {
                const string commandText = @"
IF OBJECT_ID(N'sys.dm_db_vector_indexes') IS NULL
    SELECT CAST(0 AS bit);
ELSE
    EXEC(N'
        SELECT CASE WHEN EXISTS (
            SELECT 1
            FROM sys.dm_db_vector_indexes
            WHERE object_id = OBJECT_ID(N''dbo.VectorSearchParam'')
        ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END;');";

                using var command = new SqlCommand(commandText);
                IReadOnlyList<bool> results = await _sqlRetryService.ExecuteReaderAsync(
                    command,
                    reader => reader.GetBoolean(0),
                    _logger,
                    "Probing SQL engine support for approximate vector indexes.",
                    cancellationToken,
                    isReadOnly: true);

                SupportsApproximateVectorIndex = results.Count > 0 && results[0];
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                SupportsApproximateVectorIndex = false;
                _logger.LogDebug(
                    exception,
                    "Unable to determine approximate vector index availability.");
            }
        }

        private void LogApproximateIndexFallbackIfNeeded()
        {
            if (_configuration.EnableApproximateVectorIndex &&
                !SupportsApproximateVectorIndex &&
                Interlocked.Exchange(ref _approximateUnavailableLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "Approximate vector indexing is enabled but unavailable on the connected SQL engine; vector search will use the exact search baseline.");
            }
        }

        private async Task<(short EmbeddingModelId, int Dimension)> UpsertEmbeddingModelAsync(CancellationToken cancellationToken)
        {
            const string commandText = @"
INSERT INTO dbo.EmbeddingModel (ModelName, ModelVersion, Dimension, DistanceMetric, ChunkSizeTokens, ChunkOverlapTokens)
   SELECT @ModelName, @ModelVersion, @Dimension, @DistanceMetric, @ChunkSizeTokens, @ChunkOverlapTokens
    WHERE NOT EXISTS (
        SELECT 1
        FROM dbo.EmbeddingModel WITH (UPDLOCK, HOLDLOCK)
       WHERE ModelName = @ModelName AND ModelVersion = @ModelVersion
           AND ChunkSizeTokens = @ChunkSizeTokens AND ChunkOverlapTokens = @ChunkOverlapTokens);
 
SELECT EmbeddingModelId, Dimension
FROM dbo.EmbeddingModel
WHERE ModelName = @ModelName AND ModelVersion = @ModelVersion
   AND ChunkSizeTokens = @ChunkSizeTokens AND ChunkOverlapTokens = @ChunkOverlapTokens;";

            using var command = new SqlCommand(commandText);
            command.Parameters.Add("@ModelName", SqlDbType.VarChar, 128).Value = _configuration.EmbeddingDeploymentName;
            command.Parameters.Add("@ModelVersion", SqlDbType.VarChar, 64).Value = _configuration.EmbeddingModelVersion;
            command.Parameters.Add("@ChunkSizeTokens", SqlDbType.Int).Value = _configuration.ChunkSizeTokens;
            command.Parameters.Add("@ChunkOverlapTokens", SqlDbType.Int).Value = _configuration.ChunkOverlapTokens;
            command.Parameters.Add("@Dimension", SqlDbType.Int).Value = _configuration.EmbeddingDimensions;
            command.Parameters.Add("@DistanceMetric", SqlDbType.VarChar, 16).Value = DistanceMetric;

            IReadOnlyList<(short EmbeddingModelId, int Dimension)> results = await _sqlRetryService.ExecuteReaderAsync(
                command,
                reader => (reader.GetInt16(0), reader.GetInt32(1)),
                _logger,
                "Recording the configured vector search embedding model.",
                cancellationToken,
                isReadOnly: false);

            if (results.Count == 0)
            {
                throw new InvalidOperationException("The configured embedding model could not be recorded in dbo.EmbeddingModel.");
            }

            return results[0];
        }
    }
}
