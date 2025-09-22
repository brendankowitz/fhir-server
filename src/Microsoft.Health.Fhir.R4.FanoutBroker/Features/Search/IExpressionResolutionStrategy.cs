// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Interface for expression-based resolution strategies that can handle complex FHIR search scenarios
    /// including iterative includes, wildcard includes, and metadata-driven chained searches.
    /// This provides a more sophisticated approach compared to query parameter-based strategies.
    /// </summary>
    public interface IExpressionResolutionStrategy
    {
        /// <summary>
        /// Processes chained search expressions for distributed resolution across multiple FHIR servers.
        /// Uses rich expression metadata to accurately resolve references with proper target type handling.
        /// </summary>
        /// <param name="searchExpression">The search expression containing chained search parameters.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A modified expression where chained searches are replaced with resolved reference filters.</returns>
        Task<Expression> ProcessChainedSearchAsync(
            Expression searchExpression,
            CancellationToken cancellationToken);

        /// <summary>
        /// Processes include expressions for distributed resolution across multiple FHIR servers.
        /// Handles complex scenarios like iterative includes, wildcard includes, reverse includes,
        /// and security scope filtering using rich expression metadata.
        /// </summary>
        /// <param name="searchExpression">The search expression containing include parameters.</param>
        /// <param name="mainSearchResult">The main search result to extract references from.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>A search result containing both main results and resolved include resources.</returns>
        Task<SearchResult> ProcessIncludesAsync(
            Expression searchExpression,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken);

        /// <summary>
        /// Determines if this strategy can handle the given search expression.
        /// This allows for strategy selection based on expression complexity.
        /// </summary>
        /// <param name="searchExpression">The search expression to evaluate.</param>
        /// <returns>True if this strategy can process the expression, false otherwise.</returns>
        bool CanProcess(Expression searchExpression);
    }
}