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
        /// Gets or sets a value indicating whether the preview approximate vector index may be used. This requires
        /// a vector index on dbo.VectorSearchParam and applies only to type-scoped vector searches with no other
        /// restricting predicate.
        /// </summary>
        public bool EnableApproximateVectorIndex { get; set; }

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
        /// Version tag recorded alongside the model name in dbo.EmbeddingModel. Bump this when the
        /// underlying model behind <see cref="EmbeddingDeploymentName"/> changes so that previously
        /// persisted embeddings remain attributable to the model that produced them.
        /// </summary>
        public string EmbeddingModelVersion { get; set; } = "1";

        /// <summary>
        /// Gets or sets the target size in tokens of each text chunk submitted for embedding. Changing this value
        /// yields a new dbo.EmbeddingModel row and causes existing vectors to be re-embedded by the background job.
        /// </summary>
        public int ChunkSizeTokens { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the number of tokens each chunk repeats from the previous chunk so that meaning spanning a
        /// boundary is not lost. Changing this value yields a new dbo.EmbeddingModel row and causes existing vectors
        /// to be re-embedded by the background job.
        /// </summary>
        public int ChunkOverlapTokens { get; set; } = 100;

        /// <summary>
        /// Gets or sets the maximum number of chunks emitted for a single resource and search parameter pair.
        /// When the cap is reached, remaining text is dropped to bound embedding calls, rate-limit usage, and spend.
        /// </summary>
        public int MaxChunksPerSearchParameter { get; set; } = 64;

        /// <summary>
        /// Gets or sets the default number of nearest neighbors to return.
        /// </summary>
        public int DefaultK { get; set; } = 200;

        /// <summary>
        /// Gets or sets the maximum number of nearest neighbors that can be requested.
        /// </summary>
        public int MaxK { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the vector query oversampling factor.
        /// </summary>
        public double OversamplingFactor { get; set; } = 2.0;

        /// <summary>
        /// Gets or sets the maximum query text length.
        /// </summary>
        public int MaxQueryTextLength { get; set; } = 4096;

        /// <summary>
        /// Gets or sets the maximum number of transactions covered by a single embedding-generation job.
        /// </summary>
        // Asynchronous-mode knob: max transactions per EmbeddingGeneration job definition.
        // Watchdog period/lease are tuned via the standard dbo.Parameters watchdog rows, not here.
        public int JobBatchSize { get; set; } = 500;
    }
}
