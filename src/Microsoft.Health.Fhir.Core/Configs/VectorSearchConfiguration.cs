// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Core.Configs
{
    /// <summary>
    /// Defines when embeddings are persisted for vector search.
    /// </summary>
    public enum EmbeddingPersistenceMode
    {
        /// <summary>
        /// Persists embeddings synchronously with the resource write.
        /// </summary>
        Synchronous,

        /// <summary>
        /// Persists embeddings asynchronously after the resource write.
        /// </summary>
        Asynchronous,
    }

    /// <summary>
    /// Configures vector search behavior.
    /// </summary>
    public class VectorSearchConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether vector search is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether vector search embedding calls are suspended.
        /// </summary>
        // Kill switch: embedding calls fail fast with 503 at read/write paths without a restart.
        public bool Suspended { get; set; }

        /// <summary>
        /// Gets or sets the embedding persistence mode.
        /// </summary>
        public EmbeddingPersistenceMode EmbeddingPersistenceMode { get; set; } = EmbeddingPersistenceMode.Synchronous;

        /// <summary>
        /// Gets or sets the embedding endpoint.
        /// </summary>
        public Uri EmbeddingEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the embedding deployment name.
        /// </summary>
        public string EmbeddingDeploymentName { get; set; }

        /// <summary>
        /// Gets or sets the embedding dimensions.
        /// </summary>
        public int EmbeddingDimensions { get; set; } = 1536;

        /// <summary>
        /// Gets or sets the default number of nearest neighbors to return.
        /// </summary>
        public int DefaultK { get; set; } = 200;

        /// <summary>
        /// Gets or sets the vector query oversampling factor.
        /// </summary>
        public double OversamplingFactor { get; set; } = 2.0;

        /// <summary>
        /// Gets or sets the maximum query text length.
        /// </summary>
        public int MaxQueryTextLength { get; set; } = 4096;

        /// <summary>
        /// Gets or sets the maximum number of resources per embedding generation job definition.
        /// </summary>
        // Asynchronous-mode knob: max resources per EmbeddingGeneration job definition.
        // Watchdog period/lease are tuned via the standard dbo.Parameters watchdog rows, not here.
        public int JobBatchSize { get; set; } = 500;
    }
}
