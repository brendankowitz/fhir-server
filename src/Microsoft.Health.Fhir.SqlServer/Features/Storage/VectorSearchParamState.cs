// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.SqlServer.Features.Storage
{
    internal sealed class VectorSearchParamState
    {
        internal VectorSearchParamState(
            short searchParamId,
            short chunkOrdinal,
            byte[] sourceTextHash,
            short embeddingModelId)
        {
            SearchParamId = searchParamId;
            ChunkOrdinal = chunkOrdinal;
            SourceTextHash = sourceTextHash;
            EmbeddingModelId = embeddingModelId;
        }

        internal short SearchParamId { get; }

        internal short ChunkOrdinal { get; }

        internal byte[] SourceTextHash { get; }

        internal short EmbeddingModelId { get; }
    }
}
