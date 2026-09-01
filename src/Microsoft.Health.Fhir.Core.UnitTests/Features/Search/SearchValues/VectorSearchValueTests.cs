// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.SearchValues
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class VectorSearchValueTests
    {
        [Fact]
        public void GivenSourceText_HashIsDeterministicSha256()
        {
            var a = new VectorSearchValue("shortness of breath", chunkOrdinal: 0);
            var b = new VectorSearchValue("shortness of breath", chunkOrdinal: 0);

            Assert.Equal(32, a.SourceTextHash.Length);
            Assert.Equal(a.SourceTextHash, b.SourceTextHash);
            Assert.Null(a.Embedding);
            Assert.False(a.IsValidAsCompositeComponent);
        }

        [Fact]
        public void GivenEmbeddingSet_ValueExposesIt()
        {
            var value = new VectorSearchValue("text", 0);
            value.SetEmbedding(new float[] { 0.1f, 0.2f });

            Assert.Equal(2, value.Embedding.Length);
        }

        [Fact]
        public void GivenWhitespaceText_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new VectorSearchValue("   ", 0));
        }
    }
}
