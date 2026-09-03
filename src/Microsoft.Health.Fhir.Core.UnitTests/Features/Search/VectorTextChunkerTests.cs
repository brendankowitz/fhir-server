// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Linq;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class VectorTextChunkerTests
    {
        [Fact]
        public void GivenTheSameInputs_WhenChunkIsCalledTwice_ThenResultsAreIdentical()
        {
            var chunker = CreateChunker(chunkSizeTokens: 3, chunkOverlapTokens: 1);
            const string text = "abcdefghijklmnopqrstuvwxyz";

            var first = chunker.Chunk(text, 100);
            var second = chunker.Chunk(text, 100);

            Assert.Equal(first, second);
        }

        [Fact]
        public void GivenOverlap_WhenTextIsChunked_ThenConsecutiveChunksShareExactlyTheOverlap()
        {
            var chunker = CreateChunker(chunkSizeTokens: 3, chunkOverlapTokens: 1);

            var chunks = chunker.Chunk("abcdefghijklmnopqrstuvwxyz", 100);

            Assert.True(chunks.Count > 1);
            for (int i = 1; i < chunks.Count; i++)
            {
                Assert.Equal(chunks[i - 1].Substring(8, 4), chunks[i].Substring(0, 4));
            }
        }

        [Fact]
        public void GivenDisabledChunking_WhenTextExceedsInputLimit_ThenSingleTruncatedChunkIsReturned()
        {
            var chunker = CreateChunker(chunkSizeTokens: 0, chunkOverlapTokens: 0);
            const string text = "abcdefghijklmnopqrst";

            var chunk = Assert.Single(chunker.Chunk(text, 3));

            Assert.Equal(VectorSearchValue.Truncate(text, 3), chunk);
        }

        [Fact]
        public void GivenOverlapAtLeastChunkSize_WhenTextIsChunked_ThenChunkingTerminatesWithPositiveAdvance()
        {
            var chunker = CreateChunker(chunkSizeTokens: 2, chunkOverlapTokens: 20);

            var chunks = chunker.Chunk("abcdefghijklmnopqrst", 100);

            Assert.Equal(4, chunks.Count);
            Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 8));
        }

        [Fact]
        public void GivenAChunkCap_WhenTextRequiresMoreChunks_ThenCapIsEnforced()
        {
            var chunker = CreateChunker(chunkSizeTokens: 1, chunkOverlapTokens: 0, maxChunks: 3);

            var chunks = chunker.Chunk("abcdefghijklmnopqrst", 100);

            Assert.Equal(3, chunks.Count);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \t\r\n")]
        public void GivenNullOrWhitespaceText_WhenChunked_ThenNoChunksAreReturned(string text)
        {
            var chunker = CreateChunker();

            var chunks = chunker.Chunk(text, 100);

            Assert.Empty(chunks);
        }

        [Fact]
        public void GivenTextShorterThanAChunk_WhenChunked_ThenExactlyOneUnchangedChunkIsReturned()
        {
            var chunker = CreateChunker(chunkSizeTokens: 10);
            const string text = "short text";

            var chunk = Assert.Single(chunker.Chunk(text, 100));

            Assert.Equal(text, chunk);
        }

        [Fact]
        public void GivenWhitespaceAlignedWithAChunk_WhenChunked_ThenNoWhitespaceOnlyChunkIsEmitted()
        {
            var chunker = CreateChunker(chunkSizeTokens: 1, chunkOverlapTokens: 0);

            var chunks = chunker.Chunk("abcd    efgh", 100);

            Assert.Equal(new[] { "abcd", "efgh" }, chunks);
            Assert.All(chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk)));
        }

        [Fact]
        public void GivenAnInputTokenLimit_WhenTextIsChunked_ThenOnlyTheBoundedPrefixIsConsidered()
        {
            var chunker = CreateChunker(chunkSizeTokens: 2, chunkOverlapTokens: 0);
            const string text = "abcdefghijklmnopqrstuvwxyz";

            var chunks = chunker.Chunk(text, 3);

            Assert.Equal("abcdefghijkl", string.Concat(chunks));
        }

        private static VectorTextChunker CreateChunker(
            int chunkSizeTokens = 4,
            int chunkOverlapTokens = 1,
            int maxChunks = 64)
        {
            var configuration = new VectorSearchConfiguration
            {
                ChunkSizeTokens = chunkSizeTokens,
                ChunkOverlapTokens = chunkOverlapTokens,
                MaxChunksPerSearchParameter = maxChunks,
            };
            var options = Substitute.For<IOptions<VectorSearchConfiguration>>();
            options.Value.Returns(configuration);
            return new VectorTextChunker(options);
        }
    }
}
