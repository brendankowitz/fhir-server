// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Logging.Metrics
{
    /// <summary>
    /// Emits operational metrics for vector embedding and search.
    /// </summary>
    public interface IVectorSearchMetricHandler
    {
        /// <summary>
        /// Emits the outcome and latency of an embedding call.
        /// </summary>
        /// <param name="notification">The embedding call measurements.</param>
        void EmitEmbedding(VectorEmbeddingMetricNotification notification);

        /// <summary>
        /// Emits rows written and unchanged resources skipped by a background embedding job.
        /// </summary>
        /// <param name="notification">The background job measurements.</param>
        void EmitBackgroundJob(VectorBackgroundJobMetricNotification notification);

        /// <summary>
        /// Emits the shape of a generated vector search query.
        /// </summary>
        /// <param name="notification">The vector query measurements.</param>
        void EmitQuery(VectorSearchQueryMetricNotification notification);
    }
}
