// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.SearchValues
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class VectorSearchValueTests
    {
        [Fact]
        public void GivenSourceText_WhenConstructed_ThenComputesExpectedSha256Hash()
        {
            // A change to the hash algorithm or its text encoding should make this test fail.
            var value = new VectorSearchValue("shortness of breath", chunkOrdinal: 0);
            var expectedHash = new byte[]
            {
                0x8E, 0x11, 0xAC, 0x43, 0xDB, 0xCA, 0x88, 0xA2,
                0x0E, 0x82, 0x31, 0xDE, 0x90, 0xE7, 0xDC, 0x9D,
                0x25, 0xDA, 0x77, 0x6B, 0x84, 0x9D, 0x56, 0x94,
                0xAF, 0xEC, 0xFA, 0xBD, 0x6A, 0xDC, 0xC5, 0x64,
            };

            Assert.Equal(expectedHash, value.SourceTextHash);
            Assert.Null(value.Embedding);
            Assert.False(value.IsValidAsCompositeComponent);
        }

        [Fact]
        public void GivenEmbedding_WhenSet_ThenValueExposesIt()
        {
            var value = new VectorSearchValue("text", 0);

            value.SetEmbedding(new float[] { 0.1f, 0.2f });

            Assert.Equal(2, value.Embedding.Length);
        }

        [Fact]
        public void GivenNullEmbedding_WhenSet_ThenThrowsArgumentNullException()
        {
            var value = new VectorSearchValue("text", 0);

            Assert.Throws<ArgumentNullException>(() => value.SetEmbedding(null));
        }

        [Fact]
        public void GivenWhitespaceText_WhenConstructed_ThenThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new VectorSearchValue("   ", 0));
        }

        [Fact]
        public void GivenNegativeChunkOrdinal_WhenConstructed_ThenThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new VectorSearchValue("text", -1));
        }

        [Fact]
        public void GivenVisitor_WhenAccepted_ThenDispatchesVectorSearchValue()
        {
            var value = new VectorSearchValue("text", 0);
            var visitor = Substitute.For<ISearchValueVisitor>();

            value.AcceptVisitor(visitor);

            visitor.Received(1).Visit(value);
        }

        [Fact]
        public void GivenNullVisitor_WhenAccepted_ThenThrowsArgumentNullException()
        {
            var value = new VectorSearchValue("text", 0);

            Assert.Throws<ArgumentNullException>(() => value.AcceptVisitor(null));
        }

        [Fact]
        public void GivenVectorSearchValues_WhenCompared_ThenIdentityIsSourceTextAndChunkOrdinal()
        {
            var value = new VectorSearchValue("text", 0);
            var equalValue = new VectorSearchValue("text", 0);
            var differentSourceText = new VectorSearchValue("different text", 0);
            var differentChunkOrdinal = new VectorSearchValue("text", 1);
            ISearchValue differentSearchValueType = new StringSearchValue("text");
            var hashCode = value.GetHashCode();

            value.SetEmbedding(new float[] { 0.1f, 0.2f });

            Assert.True(value.Equals(equalValue));
            Assert.False(value.Equals(differentSourceText));
            Assert.False(value.Equals(differentChunkOrdinal));
            Assert.False(value.Equals(null));
            Assert.False(value.Equals(differentSearchValueType));
            Assert.Equal(hashCode, value.GetHashCode());
            Assert.Equal(value.GetHashCode(), equalValue.GetHashCode());
        }

        [Fact]
        public void GivenVectorSearchValue_WhenVisitedBySearchValueExpressionBuilderHelper_ThenThrowsInvalidOperationException()
        {
            var helper = new SearchValueExpressionBuilderHelper();
            ISearchValueVisitor visitor = helper;

            Assert.Throws<InvalidOperationException>(() => visitor.Visit(new VectorSearchValue("text", 0)));
        }
    }
}
