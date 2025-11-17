// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Determines whether to use parallel or sequential execution strategy for fanout queries
    /// based on search parameter analysis as defined in ADR 2506.
    /// </summary>
    public class ExecutionStrategyAnalyzer : IExecutionStrategyAnalyzer
    {
        private const int ParallelCountThreshold = 10;
        private const int SequentialCountThreshold = 20;

        /// <summary>
        /// Analyzes search options to determine the optimal execution strategy.
        /// </summary>
        /// <param name="searchOptions">The search options to analyze.</param>
        /// <returns>The recommended execution strategy.</returns>
        public ExecutionStrategy DetermineStrategy(SearchOptions searchOptions)
        {
            // Always use parallel for sorting requirements (global sorting across servers)
            if (HasSortParameter(searchOptions))
            {
                return ExecutionStrategy.Parallel;
            }

            // Always use parallel for chained searches (comprehensive collection needed)
            if (HasChainedSearch(searchOptions))
            {
                return ExecutionStrategy.Parallel;
            }

            // Check for exact ID searches or specific identifiers
            if (HasExactIdSearch(searchOptions) || HasSpecificIdentifierSearch(searchOptions))
            {
                return ExecutionStrategy.Parallel;
            }

            // Check count parameter - small counts favor parallel
            if (searchOptions.MaxItemCount > 0 && searchOptions.MaxItemCount <= ParallelCountThreshold)
            {
                return ExecutionStrategy.Parallel;
            }

            // Large count values favor sequential
            if (searchOptions.MaxItemCount > SequentialCountThreshold)
            {
                return ExecutionStrategy.Sequential;
            }

            // Analyze search expression for broad vs specific queries
            if (HasBroadTextSearches(searchOptions) || HasStatusBasedSearches(searchOptions))
            {
                return ExecutionStrategy.Sequential;
            }

            // Default to parallel for targeted queries
            return ExecutionStrategy.Parallel;
        }

        private bool HasSortParameter(SearchOptions searchOptions)
        {
            return searchOptions.Sort?.Any() == true;
        }

        private bool HasChainedSearch(SearchOptions searchOptions)
        {
            if (searchOptions.Expression == null)
            {
                return false;
            }

            var chainVisitor = new ChainSearchDetectorVisitor();
            searchOptions.Expression.AcceptVisitor(chainVisitor, null);
            return chainVisitor.HasChainedSearch;
        }

        private bool HasExactIdSearch(SearchOptions searchOptions)
        {
            if (searchOptions.Expression == null)
            {
                return false;
            }

            var idVisitor = new IdSearchDetectorVisitor();
            searchOptions.Expression.AcceptVisitor(idVisitor, null);
            return idVisitor.HasExactIdSearch;
        }

        private bool HasSpecificIdentifierSearch(SearchOptions searchOptions)
        {
            if (searchOptions.Expression == null)
            {
                return false;
            }

            var identifierVisitor = new IdentifierSearchDetectorVisitor();
            searchOptions.Expression.AcceptVisitor(identifierVisitor, null);
            return identifierVisitor.HasSpecificIdentifier;
        }

        private bool HasBroadTextSearches(SearchOptions searchOptions)
        {
            if (searchOptions.Expression == null)
            {
                return false;
            }

            var textVisitor = new TextSearchDetectorVisitor();
            searchOptions.Expression.AcceptVisitor(textVisitor, null);
            return textVisitor.HasBroadTextSearch;
        }

        private bool HasStatusBasedSearches(SearchOptions searchOptions)
        {
            if (searchOptions.Expression == null)
            {
                return false;
            }

            var statusVisitor = new StatusSearchDetectorVisitor();
            searchOptions.Expression.AcceptVisitor(statusVisitor, null);
            return statusVisitor.HasStatusSearch;
        }
    }
}
