// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Logging.Metrics
{
    /// <summary>
    /// Identifies the request path that made an embedding call.
    /// </summary>
    public enum VectorEmbeddingOperation
    {
        /// <summary>
        /// Embeds vector query text while serving a search request.
        /// </summary>
        Read,

        /// <summary>
        /// Embeds extracted resource text for persistence.
        /// </summary>
        Write,

        /// <summary>
        /// Embeds extracted resource text in the background.
        /// </summary>
        BackgroundWrite,
    }

    /// <summary>
    /// Identifies whether an embedding call completed successfully.
    /// </summary>
    public enum VectorEmbeddingOutcome
    {
        /// <summary>
        /// The embedding call completed and returned the expected result.
        /// </summary>
        Success,

        /// <summary>
        /// The embedding call failed or returned an invalid result.
        /// </summary>
        Failure,
    }

    /// <summary>
    /// Contains measurements for one embedding call.
    /// </summary>
    public sealed class VectorEmbeddingMetricNotification
    {
        /// <summary>
        /// Gets or sets the request path that made the embedding call.
        /// </summary>
        public VectorEmbeddingOperation Operation { get; set; }

        /// <summary>
        /// Gets or sets the embedding call outcome.
        /// </summary>
        public VectorEmbeddingOutcome Outcome { get; set; }

        /// <summary>
        /// Gets or sets the elapsed call time in milliseconds.
        /// </summary>
        public long ElapsedMilliseconds { get; set; }
    }
}
