// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Health.Fhir.SqlServer.Features.Storage
{
    /// <summary>
    /// Provides the SQL engine capabilities and embedding model identity used by vector search.
    /// </summary>
    public interface IVectorSearchCapabilityProvider
    {
        /// <summary>
        /// Gets a value indicating whether vector search is available.
        /// </summary>
        bool IsVectorSearchAvailable { get; }

        /// <summary>
        /// Gets a value indicating whether an approximate vector index is available for dbo.VectorSearchParam.
        /// </summary>
        bool SupportsApproximateVectorIndex { get; }

        /// <summary>
        /// Gets the identifier of the configured embedding model.
        /// </summary>
        short EmbeddingModelId { get; }

        /// <summary>
        /// Ensures that vector search capabilities and model identity have been initialized.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task EnsureInitializedAsync(CancellationToken cancellationToken);
    }
}
