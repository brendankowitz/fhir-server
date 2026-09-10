// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class SearchResultEntryTests
    {
        private static readonly ResourceWrapper Resource = new ResourceWrapper(
            "resource-id",
            "1",
            "Patient",
            rawResource: null,
            request: null,
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            deleted: false,
            searchIndices: null,
            compartmentIndices: null,
            lastModifiedClaims: null);

        [Fact]
        public void GivenEqualScores_WhenComparingSearchResultEntries_ThenEntriesAndHashesAreEqual()
        {
            var left = new SearchResultEntry(Resource, SearchEntryMode.Match, 0.75m);
            var right = new SearchResultEntry(Resource, SearchEntryMode.Match, 0.75m);

            Assert.True(left.Equals(right));
            Assert.True(left.Equals((object)right));
            Assert.True(left == right);
            Assert.False(left != right);
            Assert.Equal(left.GetHashCode(), right.GetHashCode());
        }

        [Fact]
        public void GivenDifferentScores_WhenComparingSearchResultEntries_ThenEntriesAndHashesAreDifferent()
        {
            var left = new SearchResultEntry(Resource, SearchEntryMode.Match, 0.75m);
            var right = new SearchResultEntry(Resource, SearchEntryMode.Match, 0.5m);

            Assert.False(left.Equals(right));
            Assert.False(left.Equals((object)right));
            Assert.False(left == right);
            Assert.True(left != right);
            Assert.NotEqual(left.GetHashCode(), right.GetHashCode());
        }
    }
}
