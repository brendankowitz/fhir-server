// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Search;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Resolution modes for chained searches and includes.
    /// </summary>
    public enum ResolutionMode
    {
        /// <summary>
        /// Assumes resources and their references are co-located on the same servers.
        /// Optimal performance for legacy deployments.
        /// </summary>
        Passthrough,

        /// <summary>
        /// Executes comprehensive cross-shard resolution for distributed scenarios.
        /// Higher latency but complete results.
        /// </summary>
        Distributed,
    }

    /// <summary>
    /// Represents different resolution strategies for handling chained searches and includes
    /// across multiple FHIR servers with different data distribution patterns.
    /// </summary>
    public interface IResolutionStrategy
    {
        /// <summary>
        /// Processes chained search queries according to the resolution strategy.
        /// </summary>
        /// <param name="resourceType">The target resource type for the search.</param>
        /// <param name="queryParameters">The query parameters including chained expressions.</param>
        /// <param name="cancellationToken">Cancellation token with timeout protection.</param>
        /// <returns>Modified query parameters that replace chained expressions with resolved filters.</returns>
        Task<IReadOnlyList<Tuple<string, string>>> ProcessChainedSearchAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken);

        /// <summary>
        /// Processes include/revinclude operations according to the resolution strategy.
        /// </summary>
        /// <param name="resourceType">The resource type being searched.</param>
        /// <param name="queryParameters">The query parameters including include expressions.</param>
        /// <param name="mainSearchResult">The main search result to enhance with includes.</param>
        /// <param name="cancellationToken">Cancellation token with timeout protection.</param>
        /// <returns>Enhanced search result with included resources.</returns>
        Task<SearchResult> ProcessIncludesAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken);
    }
}