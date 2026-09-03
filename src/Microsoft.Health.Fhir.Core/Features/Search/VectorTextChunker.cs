// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using EnsureThat;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Splits text into deterministic, overlapping character windows using four characters per token.
    /// </summary>
    public class VectorTextChunker : IVectorTextChunker
    {
        private const int CharactersPerToken = 4;
        private readonly VectorSearchConfiguration _configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="VectorTextChunker"/> class.
        /// </summary>
        /// <param name="options">The vector search configuration.</param>
        public VectorTextChunker(IOptions<VectorSearchConfiguration> options)
        {
            EnsureArg.IsNotNull(options, nameof(options));
            _configuration = options.Value;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> Chunk(string text, int maxInputTokens)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            string boundedText = VectorSearchValue.Truncate(text, maxInputTokens);
            if (string.IsNullOrWhiteSpace(boundedText))
            {
                return Array.Empty<string>();
            }

            int chunkSizeTokens = _configuration.ChunkSizeTokens;
            if (chunkSizeTokens <= 0)
            {
                return new[] { boundedText };
            }

            int maxChunks = _configuration.MaxChunksPerSearchParameter;
            if (maxChunks <= 0)
            {
                return Array.Empty<string>();
            }

            long chunkSizeCharacters = (long)chunkSizeTokens * CharactersPerToken;
            if (boundedText.Length <= chunkSizeCharacters)
            {
                return new[] { boundedText };
            }

            int overlapTokens = Math.Max(0, _configuration.ChunkOverlapTokens);
            overlapTokens = Math.Min(overlapTokens, chunkSizeTokens - 1);
            long advanceCharacters = (long)(chunkSizeTokens - overlapTokens) * CharactersPerToken;

            var chunks = new List<string>(Math.Min(maxChunks, 16));
            for (long start = 0; start < boundedText.Length && chunks.Count < maxChunks; start += advanceCharacters)
            {
                int length = (int)Math.Min(chunkSizeCharacters, boundedText.Length - start);
                string chunk = boundedText.Substring((int)start, length);
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    chunks.Add(chunk);
                }

                if (start + length >= boundedText.Length)
                {
                    break;
                }
            }

            return chunks;
        }
    }
}
