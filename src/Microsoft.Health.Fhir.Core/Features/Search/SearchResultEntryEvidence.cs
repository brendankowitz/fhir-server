// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Identifies the source chunk that supplied a vector search match.
    /// </summary>
    public readonly struct SearchResultEntryEvidence : IEquatable<SearchResultEntryEvidence>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SearchResultEntryEvidence"/> struct.
        /// </summary>
        /// <param name="text">The source chunk text.</param>
        /// <param name="chunkOrdinal">The zero-based chunk ordinal.</param>
        public SearchResultEntryEvidence(string text, int chunkOrdinal)
        {
            Text = EnsureArg.IsNotNull(text, nameof(text));
            ChunkOrdinal = chunkOrdinal;
        }

        /// <summary>
        /// Gets the source chunk text.
        /// </summary>
        public string Text { get; }

        /// <summary>
        /// Gets the zero-based chunk ordinal.
        /// </summary>
        public int ChunkOrdinal { get; }

        /// <summary>
        /// Determines whether two evidence values are equal.
        /// </summary>
        public static bool operator ==(SearchResultEntryEvidence left, SearchResultEntryEvidence right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Determines whether two evidence values are unequal.
        /// </summary>
        public static bool operator !=(SearchResultEntryEvidence left, SearchResultEntryEvidence right)
        {
            return !left.Equals(right);
        }

        /// <inheritdoc/>
        public bool Equals(SearchResultEntryEvidence other)
        {
            return string.Equals(Text, other.Text, StringComparison.Ordinal) && ChunkOrdinal == other.ChunkOrdinal;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is SearchResultEntryEvidence other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return HashCode.Combine(Text, ChunkOrdinal);
        }
    }
}
