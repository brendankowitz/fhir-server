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
    /// Interface for processing _include and _revinclude parameters across multiple FHIR servers.
    /// </summary>
    public interface IIncludeProcessor
    {
        /// <summary>
        /// Detects if the query parameters contain _include or _revinclude parameters.
        /// </summary>
        /// <param name="queryParameters">The query parameters to analyze.</param>
        /// <returns>True if include/revinclude parameters are present.</returns>
        bool HasIncludeParameters(IReadOnlyList<Tuple<string, string>> queryParameters);

        /// <summary>
        /// Processes include/revinclude parameters for fanout broker search.
        /// This method handles the aggregation of included resources from multiple servers.
        /// </summary>
        /// <param name="resourceType">The primary resource type being searched.</param>
        /// <param name="queryParameters">The original query parameters including include/revinclude.</param>
        /// <param name="mainSearchResult">The main search result from the primary query.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A search result with included resources populated.</returns>
        Task<SearchResult> ProcessIncludesAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken);

        /// <summary>
        /// Handles the $includes operation for paginated retrieval of included resources.
        /// This is called when the number of included resources exceeds the limit.
        /// </summary>
        /// <param name="resourceType">The resource type for the $includes operation.</param>
        /// <param name="queryParameters">The query parameters for the $includes operation.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A bundle containing only the included resources with pagination support.</returns>
        Task<SearchResult> ProcessIncludesOperationAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken);
    }
}