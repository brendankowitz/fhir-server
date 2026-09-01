// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using EnsureThat;

namespace Microsoft.Health.Fhir.Core.Features.Search.SearchValues
{
    /// <summary>
    /// A search value representing text to be (or already) embedded for vector similarity search.
    /// Embedding is null until filled by the synchronous enricher or the asynchronous drain.
    /// </summary>
    public class VectorSearchValue : ISearchValue
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="VectorSearchValue"/> class.
        /// </summary>
        /// <param name="sourceText">The text to be embedded.</param>
        /// <param name="chunkOrdinal">The zero-based ordinal of the chunk in the source text.</param>
        public VectorSearchValue(string sourceText, int chunkOrdinal)
        {
            EnsureArg.IsNotNullOrWhiteSpace(sourceText, nameof(sourceText));
            EnsureArg.IsGte(chunkOrdinal, 0, nameof(chunkOrdinal));

            SourceText = sourceText;
            ChunkOrdinal = chunkOrdinal;
            SourceTextHash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceText));
        }

        /// <summary>
        /// Gets the text to be embedded.
        /// </summary>
        public string SourceText { get; }

        /// <summary>
        /// Gets the zero-based ordinal of the chunk in the source text.
        /// </summary>
        public int ChunkOrdinal { get; }

        /// <summary>
        /// Gets the SHA-256 hash of the source text.
        /// </summary>
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The required search value API exposes the SHA-256 hash as a byte array.")]
        public byte[] SourceTextHash { get; }

        /// <summary>
        /// Gets the embedding associated with the source text.
        /// </summary>
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The required search value API exposes embeddings as float arrays.")]
        public float[] Embedding { get; private set; }

        /// <inheritdoc />
        public bool IsValidAsCompositeComponent => false;

        /// <summary>
        /// Truncates text to the character budget approximated by the maximum token count.
        /// </summary>
        /// <param name="text">The text to truncate.</param>
        /// <param name="maxTokens">The maximum number of tokens approximated by the character budget.</param>
        /// <returns>The original text when it fits the budget; otherwise, its truncated prefix.</returns>
        public static string Truncate(string text, int maxTokens)
        {
            EnsureArg.IsNotNull(text, nameof(text));
            EnsureArg.IsGte(maxTokens, 0, nameof(maxTokens));

            int maxCharacters = maxTokens * 4;
            return text.Length <= maxCharacters ? text : text.Substring(0, maxCharacters);
        }

        /// <summary>
        /// Sets the embedding associated with the source text.
        /// </summary>
        /// <param name="embedding">The embedding to associate with the source text.</param>
        public void SetEmbedding(float[] embedding)
        {
            EnsureArg.IsNotNull(embedding, nameof(embedding));

            Embedding = embedding;
        }

        /// <inheritdoc />
        public void AcceptVisitor(ISearchValueVisitor visitor)
        {
            EnsureArg.IsNotNull(visitor, nameof(visitor));

            visitor.Visit(this);
        }

        /// <inheritdoc />
        public bool Equals([AllowNull] ISearchValue other)
        {
            return other is VectorSearchValue vector
                && ChunkOrdinal == vector.ChunkOrdinal
                && string.Equals(SourceText, vector.SourceText, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override string ToString() => $"vector(chunk:{ChunkOrdinal})";

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(SourceText, ChunkOrdinal);
        }
    }
}
