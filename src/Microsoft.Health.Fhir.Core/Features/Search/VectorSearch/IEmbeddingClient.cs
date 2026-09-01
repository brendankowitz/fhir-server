// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Health.Fhir.Core.Features.Search.VectorSearch
{
    /// <summary>
    /// Produces embeddings for vector search. One implementation (Azure AI Foundry);
    /// the interface exists for testability, not provider pluggability (ADR 2605 §E3).
    /// </summary>
    public interface IEmbeddingClient
    {
        /// <summary>
        /// Produces embeddings for the specified inputs.
        /// </summary>
        /// <param name="inputs">The strings to embed.</param>
        /// <param name="tightBudget">
        /// True = single attempt with a short timeout, no backoff — for the best-effort
        /// Prefer-driven path where falling back to async is cheaper than waiting.
        /// False = full retry policy + circuit breaker (sync mode, async jobs, query embedding).
        /// </param>
        /// <param name="cancellationToken">The token that can cancel the operation.</param>
        /// <returns>The embeddings for the supplied inputs.</returns>
        /// <exception cref="EmbeddingUnavailableException">Surfaced as HTTP 503 by sync-mode/query callers.</exception>
        Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, bool tightBudget, CancellationToken cancellationToken);
    }
}
