// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search.ContinuationToken;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.Sorting
{
    /// <summary>
    /// Service for managing distributed sorting across multiple FHIR servers.
    /// </summary>
    public interface IDistributedSortingService
    {
        /// <summary>
        /// Performs the first page of a sorted search across multiple servers.
        /// </summary>
        /// <param name="servers">List of enabled servers to query.</param>
        /// <param name="searchOptions">Search options including sort criteria.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Aggregated and sorted search result with continuation token.</returns>
        Task<SearchResult> ExecuteFirstPageSortedSearchAsync(
            IReadOnlyList<FhirServerEndpoint> servers,
            SearchOptions searchOptions,
            CancellationToken cancellationToken);

        /// <summary>
        /// Performs subsequent pages of a sorted search using distributed continuation token.
        /// </summary>
        /// <param name="servers">List of enabled servers to query.</param>
        /// <param name="searchOptions">Search options including sort criteria.</param>
        /// <param name="distributedToken">Distributed continuation token from previous page.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Aggregated and sorted search result with updated continuation token.</returns>
        Task<SearchResult> ExecuteSubsequentPageSortedSearchAsync(
            IReadOnlyList<FhirServerEndpoint> servers,
            SearchOptions searchOptions,
            DistributedContinuationToken distributedToken,
            CancellationToken cancellationToken);

        /// <summary>
        /// Merges and re-sorts results from multiple servers globally.
        /// </summary>
        /// <param name="serverResults">Results from individual servers.</param>
        /// <param name="searchOptions">Search options including sort criteria.</param>
        /// <param name="requestedCount">Number of results requested.</param>
        /// <returns>Globally sorted results limited to requested count.</returns>
        SearchResult MergeAndSortResults(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions searchOptions,
            int requestedCount);

        /// <summary>
        /// Creates a distributed continuation token from server results.
        /// </summary>
        /// <param name="serverResults">Results from individual servers.</param>
        /// <param name="searchOptions">Search options including sort criteria.</param>
        /// <param name="returnedResults">The results being returned to the client.</param>
        /// <returns>Distributed continuation token for next page.</returns>
        DistributedContinuationToken CreateContinuationToken(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions searchOptions,
            IReadOnlyList<SearchResultEntry> returnedResults);
    }
}