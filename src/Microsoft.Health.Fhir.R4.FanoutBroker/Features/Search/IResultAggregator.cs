// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Aggregates search results from multiple FHIR servers into unified responses.
    /// </summary>
    public interface IResultAggregator
    {
        /// <summary>
        /// Aggregates results from parallel execution across multiple servers.
        /// </summary>
        /// <param name="serverResults">Results from individual servers.</param>
        /// <param name="originalSearchOptions">Original search options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Aggregated search result.</returns>
        Task<SearchResult> AggregateParallelResultsAsync(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions originalSearchOptions,
            CancellationToken cancellationToken);

        /// <summary>
        /// Aggregates results from sequential execution across multiple servers.
        /// </summary>
        /// <param name="serverResults">Results from individual servers.</param>
        /// <param name="originalSearchOptions">Original search options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Aggregated search result.</returns>
        Task<SearchResult> AggregateSequentialResultsAsync(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions originalSearchOptions,
            CancellationToken cancellationToken);

        /// <summary>
        /// Processes include and revinclude operations by fanning out ID-based queries.
        /// </summary>
        /// <param name="primaryResults">Primary search results containing references.</param>
        /// <param name="searchOptions">Original search options with include/revinclude parameters.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Updated search result with included resources.</returns>
        Task<SearchResult> ProcessIncludesAsync(
            SearchResult primaryResults,
            SearchOptions searchOptions,
            CancellationToken cancellationToken);
    }
}