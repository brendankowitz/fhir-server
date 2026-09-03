// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Splits text into deterministic, overlapping windows for vector embedding.
    /// </summary>
    public interface IVectorTextChunker
    {
        /// <summary>
        /// Splits text into chunks after applying the total input token limit.
        /// </summary>
        /// <param name="text">The text to split.</param>
        /// <param name="maxInputTokens">The maximum total input tokens to consider.</param>
        /// <returns>The chunks to embed, in ordinal order.</returns>
        IReadOnlyList<string> Chunk(string text, int maxInputTokens);
    }
}
