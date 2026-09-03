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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.EmbeddingGeneration;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Newtonsoft.Json;

namespace Microsoft.Health.Fhir.SqlServer.Features.Watchdogs
{
    /// <summary>
    /// Enqueues embedding-generation jobs for newly visible SQL transactions.
    /// The first cycle after vector search is enabled intentionally sweeps the existing database,
    /// which can produce a large, billable embedding backfill. The persisted watermark prevents
    /// that backfill from repeating after a process restart.
    /// </summary>
    internal class VectorizationWatchdog : Watchdog<VectorizationWatchdog>
    {
        private const byte QueueType = (byte)Core.Features.Operations.QueueType.EmbeddingGeneration;

        private readonly SqlStoreClient _store;
        private readonly SqlQueueClient _sqlQueueClient;
        private readonly ISqlRetryService _sqlRetryService;
        private readonly IVectorSearchCapabilityProvider _vectorSearchCapabilityProvider;
        private readonly ISearchParameterDefinitionManager _searchParameterDefinitionManager;
        private readonly ISqlServerFhirModel _model;
        private readonly VectorSearchConfiguration _configuration;
        private readonly ILogger<VectorizationWatchdog> _logger;

        public VectorizationWatchdog(
            SqlStoreClient store,
            SqlQueueClient sqlQueueClient,
            ISqlRetryService sqlRetryService,
            IVectorSearchCapabilityProvider vectorSearchCapabilityProvider,
            ISearchParameterDefinitionManager searchParameterDefinitionManager,
            ISqlServerFhirModel model,
            IOptions<VectorSearchConfiguration> configuration,
            ILogger<VectorizationWatchdog> logger)
            : base(sqlRetryService, logger)
        {
            _store = EnsureArg.IsNotNull(store, nameof(store));
            _sqlQueueClient = EnsureArg.IsNotNull(sqlQueueClient, nameof(sqlQueueClient));
            _sqlRetryService = EnsureArg.IsNotNull(sqlRetryService, nameof(sqlRetryService));
            _vectorSearchCapabilityProvider = EnsureArg.IsNotNull(vectorSearchCapabilityProvider, nameof(vectorSearchCapabilityProvider));
            _searchParameterDefinitionManager = EnsureArg.IsNotNull(searchParameterDefinitionManager, nameof(searchParameterDefinitionManager));
            _model = EnsureArg.IsNotNull(model, nameof(model));
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        internal VectorizationWatchdog()
        {
            // this is used to get param names for testing
        }

        internal VectorizationWatchdog(
            IVectorSearchCapabilityProvider vectorSearchCapabilityProvider,
            ISearchParameterDefinitionManager searchParameterDefinitionManager,
            ISqlServerFhirModel model,
            IOptions<VectorSearchConfiguration> configuration,
            ILogger<VectorizationWatchdog> logger)
        {
            _vectorSearchCapabilityProvider = EnsureArg.IsNotNull(vectorSearchCapabilityProvider, nameof(vectorSearchCapabilityProvider));
            _searchParameterDefinitionManager = EnsureArg.IsNotNull(searchParameterDefinitionManager, nameof(searchParameterDefinitionManager));
            _model = EnsureArg.IsNotNull(model, nameof(model));
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <summary>
        /// Gets the parameter identifier containing the last transaction covered by embedding-generation jobs.
        /// </summary>
        public string LastProcessedTransactionId => $"{Name}.LastProcessedTransactionId";

        /// <inheritdoc />
        public override double LeasePeriodSec { get; internal set; } = 2 * 3600;

        /// <inheritdoc />
        public override bool AllowRebalance { get; internal set; } = true;

        /// <inheritdoc />
        public override double PeriodSec { get; internal set; } = 3600;

        internal Task RunWorkForTestingAsync(CancellationToken cancellationToken) => RunWorkAsync(cancellationToken);

        protected override async Task RunWorkAsync(CancellationToken cancellationToken)
        {
            await _vectorSearchCapabilityProvider.EnsureInitializedAsync(cancellationToken);
            if (!_vectorSearchCapabilityProvider.IsVectorSearchAvailable)
            {
                _logger.LogInformation($"{Name}: vector search is unavailable.");
                return;
            }

            _logger.LogInformation($"{Name}: starting...");
            long visibility = await GetTransactionVisibilityAsync(cancellationToken);
            _logger.LogInformation($"{Name}: visibility={visibility}.");

            if (_configuration.JobBatchSize <= 0)
            {
                throw new InvalidOperationException($"{nameof(VectorSearchConfiguration.JobBatchSize)} must be greater than zero.");
            }

            short[] resourceTypeIds = _searchParameterDefinitionManager.AllSearchParameters
                .Where(parameter => parameter.VectorConfig != null && parameter.IsSearchable && parameter.IsSupported)
                .SelectMany(parameter => parameter.BaseResourceTypes)
                .Distinct(StringComparer.Ordinal)
                .Select(GetResourceTypeId)
                .Where(resourceTypeId => resourceTypeId.HasValue)
                .Select(resourceTypeId => resourceTypeId.Value)
                .ToArray();

            if (resourceTypeIds.Length == 0)
            {
                _logger.LogInformation($"{Name}: no vector-enabled resource types were found.");
                return;
            }

            while (true)
            {
                long lastTransactionId = await GetLastProcessedTransactionIdAsync(cancellationToken);
                _logger.LogInformation($"{Name}: last processed transaction={lastTransactionId}.");
                IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)> transactions =
                    await GetTransactionsAsync(lastTransactionId, visibility, cancellationToken);
                _logger.LogInformation($"{Name}: found transactions={transactions.Count}.");

                if (transactions.Count == 0)
                {
                    return;
                }

                long chunkStart = lastTransactionId;
                long[] orderedTransactionIds = transactions
                    .Select(transaction => transaction.TransactionId)
                    .OrderBy(transactionId => transactionId)
                    .ToArray();
                var definitions = new List<string>();

                for (int offset = 0; offset < orderedTransactionIds.Length; offset += _configuration.JobBatchSize)
                {
                    int chunkEndOffset = Math.Min(offset + _configuration.JobBatchSize, orderedTransactionIds.Length) - 1;
                    long chunkEnd = orderedTransactionIds[chunkEndOffset];

                    foreach (short resourceTypeId in resourceTypeIds)
                    {
                        var definition = new EmbeddingGenerationJobDefinition(
                            JobType.EmbeddingGenerationProcessing,
                            resourceTypeId,
                            chunkStart,
                            chunkEnd);
                        definitions.Add(JsonConvert.SerializeObject(definition));
                    }

                    chunkStart = chunkEnd;
                }

                await EnqueueAsync(definitions.ToArray(), cancellationToken);

                long processedThrough = orderedTransactionIds[^1];
                await UpdateLastProcessedTransactionIdAsync(processedThrough);
                _logger.LogInformation($"{Name}: enqueued jobs={definitions.Count} through transaction={processedThrough}.");
            }
        }

        protected override async Task InitAdditionalParamsAsync()
        {
            await using var cmd = new SqlCommand("INSERT INTO dbo.Parameters (Id, Bigint) SELECT @Id, 5105975696064002770"); // surrogate id for the past. does not matter.
            cmd.Parameters.AddWithValue("@Id", LastProcessedTransactionId);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, CancellationToken.None);
        }

        internal virtual Task<long> GetTransactionVisibilityAsync(CancellationToken cancellationToken)
        {
            return _store.MergeResourcesGetTransactionVisibilityAsync(cancellationToken);
        }

        internal virtual Task<long> GetLastProcessedTransactionIdAsync(CancellationToken cancellationToken)
        {
            return GetLongParameterByIdAsync(LastProcessedTransactionId, cancellationToken);
        }

        internal virtual Task<IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)>> GetTransactionsAsync(
            long startNotInclusiveTransactionId,
            long endInclusiveTransactionId,
            CancellationToken cancellationToken)
        {
            return _store.GetTransactionsAsync(startNotInclusiveTransactionId, endInclusiveTransactionId, cancellationToken);
        }

        internal virtual async Task EnqueueAsync(string[] definitions, CancellationToken cancellationToken)
        {
            await _sqlQueueClient.EnqueueAsync(QueueType, definitions, null, false, cancellationToken);
        }

        internal virtual async Task UpdateLastProcessedTransactionIdAsync(long lastTransactionId)
        {
            await using var cmd = new SqlCommand(@"
UPDATE dbo.Parameters SET Bigint = @LastTranId WHERE Id = @Id
EXECUTE dbo.LogEvent @Process='UpdateLastProcessedTransactionId',@Status='Warn',@Rows=@@rowcount,@Text=@LastTranId
            ");
            cmd.Parameters.AddWithValue("@Id", LastProcessedTransactionId);
            cmd.Parameters.AddWithValue("@LastTranId", lastTransactionId);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, CancellationToken.None);
        }

        private short? GetResourceTypeId(string resourceTypeName)
        {
            return _model.TryGetResourceTypeId(resourceTypeName, out short resourceTypeId) ? resourceTypeId : null;
        }
    }
}
