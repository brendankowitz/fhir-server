// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    public readonly struct SearchResultEntry : IEquatable<SearchResultEntry>
    {
        public SearchResultEntry(
            ResourceWrapper resourceWrapper,
            SearchEntryMode searchEntryMode = SearchEntryMode.Match,
            decimal? score = null,
            SearchResultEntryEvidence? evidence = null)
        {
            EnsureArg.IsNotNull(resourceWrapper, nameof(resourceWrapper));

            Resource = resourceWrapper;
            SearchEntryMode = searchEntryMode;
            Score = score;
            Evidence = evidence;
        }

        public ResourceWrapper Resource { get; }

        public SearchEntryMode SearchEntryMode { get; }

        public decimal? Score { get; }

        public SearchResultEntryEvidence? Evidence { get; }

        public static bool operator ==(SearchResultEntry left, SearchResultEntry right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SearchResultEntry left, SearchResultEntry right)
        {
            return !(left == right);
        }

        public bool Equals(SearchResultEntry other)
        {
            return Equals(Resource, other.Resource) &&
                SearchEntryMode == other.SearchEntryMode &&
                Score == other.Score &&
                Nullable.Equals(Evidence, other.Evidence);
        }

        public override bool Equals(object obj)
        {
            return obj is SearchResultEntry other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = ((Resource != null ? Resource.GetHashCode() : 0) * 397) ^ (int)SearchEntryMode;
                hashCode = (hashCode * 397) ^ Score.GetHashCode();
                return (hashCode * 397) ^ Evidence.GetHashCode();
            }
        }
    }
}
