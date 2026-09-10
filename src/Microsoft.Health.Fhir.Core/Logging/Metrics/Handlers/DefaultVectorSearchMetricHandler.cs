// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics.Metrics;
using EnsureThat;

namespace Microsoft.Health.Fhir.Core.Logging.Metrics.Handlers
{
    /// <summary>
    /// Emits vector search metrics through the FHIR server meter.
    /// </summary>
    public sealed class DefaultVectorSearchMetricHandler : BaseMeterMetricHandler, IVectorSearchMetricHandler
    {
        private readonly Counter<long> _embeddingCalls;
        private readonly Histogram<long> _embeddingLatency;
        private readonly Counter<long> _backgroundRowsWritten;
        private readonly Counter<long> _backgroundResourcesSkipped;
        private readonly Counter<long> _queries;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultVectorSearchMetricHandler"/> class.
        /// </summary>
        /// <param name="meterFactory">The meter factory.</param>
        public DefaultVectorSearchMetricHandler(IMeterFactory meterFactory)
            : base(meterFactory)
        {
            _embeddingCalls = MetricMeter.CreateCounter<long>("VectorSearch.EmbeddingCalls");
            _embeddingLatency = MetricMeter.CreateHistogram<long>("VectorSearch.EmbeddingLatency");
            _backgroundRowsWritten = MetricMeter.CreateCounter<long>("VectorSearch.BackgroundRowsWritten");
            _backgroundResourcesSkipped = MetricMeter.CreateCounter<long>("VectorSearch.BackgroundResourcesSkipped");
            _queries = MetricMeter.CreateCounter<long>("VectorSearch.Queries");
        }

        /// <inheritdoc />
        public void EmitEmbedding(VectorEmbeddingMetricNotification notification)
        {
            EnsureArg.IsNotNull(notification, nameof(notification));

            KeyValuePair<string, object>[] tags =
            [
                new("Operation", notification.Operation.ToString()),
                new("Outcome", notification.Outcome.ToString()),
            ];
            _embeddingCalls.Add(1, tags);
            _embeddingLatency.Record(notification.ElapsedMilliseconds, tags);
        }

        /// <inheritdoc />
        public void EmitBackgroundJob(VectorBackgroundJobMetricNotification notification)
        {
            EnsureArg.IsNotNull(notification, nameof(notification));

            _backgroundRowsWritten.Add(notification.RowsWritten);
            _backgroundResourcesSkipped.Add(notification.ResourcesSkipped);
        }

        /// <inheritdoc />
        public void EmitQuery(VectorSearchQueryMetricNotification notification)
        {
            EnsureArg.IsNotNull(notification, nameof(notification));

            _queries.Add(
                1,
                new KeyValuePair<string, object>(
                    "Shape",
                    notification.IsApproximate ? "Approximate" : "Exact"));
        }
    }
}
