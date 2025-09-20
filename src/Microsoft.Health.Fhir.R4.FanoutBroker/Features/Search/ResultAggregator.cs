// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Aggregates search results from multiple FHIR servers into unified responses.
    ///
    /// IMPORTANT: Per ADR-2506, resources with identical logical IDs from different servers
    /// are ALL returned and differentiated by their fullUrl values. No deduplication is performed
    /// on resources, only on unsupported search parameters.
    /// </summary>
    public class ResultAggregator : IResultAggregator
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<ResultAggregator> _logger;

        public ResultAggregator(
            IFhirServerOrchestrator serverOrchestrator,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<ResultAggregator> logger)
        {
            _serverOrchestrator = serverOrchestrator ?? throw new ArgumentNullException(nameof(serverOrchestrator));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public Task<SearchResult> AggregateParallelResultsAsync(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions originalSearchOptions,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Aggregating parallel results from {ServerCount} servers", serverResults.Count);

            var allResults = new List<SearchResultEntry>();
            var allUnsupportedParams = new List<Tuple<string, string>>();
            var serverTokens = new List<ServerContinuationToken>();

            // Collect results from all servers and ensure proper fullUrl differentiation
            foreach (var serverResult in serverResults)
            {
                if (serverResult.SearchResult?.Results != null)
                {
                    // Ensure each result maintains its source server information in fullUrl
                    var serverEntries = EnsureServerFullUrls(serverResult.SearchResult.Results, serverResult.ServerBaseUrl);
                    allResults.AddRange(serverEntries);
                }

                if (serverResult.SearchResult?.UnsupportedSearchParameters != null)
                {
                    allUnsupportedParams.AddRange(serverResult.SearchResult.UnsupportedSearchParameters);
                }

                // Create server continuation token with sort value tracking for distributed sorting
                var serverToken = new ServerContinuationToken
                {
                    Endpoint = serverResult.ServerId,
                    Token = serverResult.SearchResult?.ContinuationToken,
                    Exhausted = string.IsNullOrEmpty(serverResult.SearchResult?.ContinuationToken),
                    ResultsReturned = serverResult.SearchResult?.Results?.Count() ?? 0,
                    LastSortValue = ExtractLastSortValue(serverResult.SearchResult, originalSearchOptions.Sort),
                };

                serverTokens.Add(serverToken);
            }

            // Remove duplicate unsupported parameters
            var uniqueUnsupportedParams = allUnsupportedParams
                .Distinct()
                .ToList();

            // Check for potential duplicate resource IDs from different servers (this is expected and correct behavior)
            var resourceIdCounts = allResults
                .GroupBy(r => r.Resource.ResourceId)
                .Where(g => g.Count() > 1)
                .ToList();

            if (resourceIdCounts.Any())
            {
                _logger.LogDebug("Found {DuplicateIdCount} resource IDs present on multiple servers (as expected per ADR-2506)",
                    resourceIdCounts.Count);
            }

            // Sort results if needed
            var finalResults = allResults;
            if (originalSearchOptions.Sort?.Any() == true)
            {
                finalResults = SortResults(allResults, originalSearchOptions.Sort);
                _logger.LogDebug("Sorted {ResultCount} results globally", finalResults.Count);
            }

            // Apply count limit
            if (originalSearchOptions.MaxItemCount > 0 && finalResults.Count > originalSearchOptions.MaxItemCount)
            {
                finalResults = finalResults.Take(originalSearchOptions.MaxItemCount).ToList();
            }

            // Create aggregated continuation token
            string aggregatedContinuationToken = null;
            if (serverTokens.Any(t => !t.Exhausted))
            {
                var fanoutToken = new FanoutContinuationToken
                {
                    Servers = serverTokens,
                    SortCriteria = SerializeSortCriteria(originalSearchOptions.Sort),
                    PageSize = originalSearchOptions.MaxItemCount,
                    ExecutionStrategy = ExecutionStrategy.Parallel.ToString(),
                    // ResourceType can be inferred from the search context
                };

                aggregatedContinuationToken = fanoutToken.ToBase64String();
            }

            var aggregatedResult = new SearchResult(
                results: finalResults,
                continuationToken: aggregatedContinuationToken,
                sortOrder: originalSearchOptions.Sort,
                unsupportedSearchParameters: uniqueUnsupportedParams);

            _logger.LogInformation(
                "Parallel aggregation completed. {ResultCount} results, continuation: {HasContinuation}",
                finalResults.Count,
                !string.IsNullOrEmpty(aggregatedContinuationToken));

            return Task.FromResult(aggregatedResult);
        }

        /// <inheritdoc />
        public Task<SearchResult> AggregateSequentialResultsAsync(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions originalSearchOptions,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Aggregating sequential results from {ServerCount} servers", serverResults.Count);

            var allResults = new List<SearchResultEntry>();
            var allUnsupportedParams = new List<Tuple<string, string>>();
            var serverTokens = new List<ServerContinuationToken>();
            var targetCount = originalSearchOptions.MaxItemCount;

            // Collect results from servers in order, respecting count limit and ensuring proper fullUrl differentiation
            foreach (var serverResult in serverResults)
            {
                if (serverResult.SearchResult?.Results != null)
                {
                    // Ensure proper fullUrl differentiation with source server base URL
                    var serverResultsList = EnsureServerFullUrls(serverResult.SearchResult.Results, serverResult.ServerBaseUrl).ToList();

                    // Apply remaining count limit
                    if (targetCount > 0)
                    {
                        var remainingCount = targetCount - allResults.Count;
                        if (remainingCount > 0)
                        {
                            var resultsTake = Math.Min(serverResultsList.Count, remainingCount);
                            allResults.AddRange(serverResultsList.Take(resultsTake));
                        }
                    }
                    else
                    {
                        allResults.AddRange(serverResultsList);
                    }
                }

                if (serverResult.SearchResult?.UnsupportedSearchParameters != null)
                {
                    allUnsupportedParams.AddRange(serverResult.SearchResult.UnsupportedSearchParameters);
                }

                // Create server continuation token with sort value tracking for distributed sorting
                var serverToken = new ServerContinuationToken
                {
                    Endpoint = serverResult.ServerId,
                    Token = serverResult.SearchResult?.ContinuationToken,
                    Exhausted = string.IsNullOrEmpty(serverResult.SearchResult?.ContinuationToken),
                    ResultsReturned = serverResult.SearchResult?.Results?.Count() ?? 0,
                    LastSortValue = ExtractLastSortValue(serverResult.SearchResult, originalSearchOptions.Sort),
                };

                serverTokens.Add(serverToken);

                // Check if we've reached the target count
                if (targetCount > 0 && allResults.Count >= targetCount)
                {
                    _logger.LogDebug("Sequential aggregation reached target count {TargetCount}", targetCount);
                    break;
                }
            }

            // Remove duplicate unsupported parameters
            var uniqueUnsupportedParams = allUnsupportedParams
                .Distinct()
                .ToList();

            // Sort results if needed (though sequential should maintain some order)
            var finalResults = allResults;
            if (originalSearchOptions.Sort?.Any() == true)
            {
                finalResults = SortResults(allResults, originalSearchOptions.Sort);
                _logger.LogDebug("Sorted {ResultCount} sequential results", finalResults.Count);
            }

            // Create aggregated continuation token
            string aggregatedContinuationToken = null;
            if (serverTokens.Any(t => !t.Exhausted))
            {
                var fanoutToken = new FanoutContinuationToken
                {
                    Servers = serverTokens,
                    SortCriteria = SerializeSortCriteria(originalSearchOptions.Sort),
                    PageSize = originalSearchOptions.MaxItemCount,
                    ExecutionStrategy = ExecutionStrategy.Sequential.ToString(),
                    // ResourceType can be inferred from the search context
                };

                aggregatedContinuationToken = fanoutToken.ToBase64String();
            }

            var aggregatedResult = new SearchResult(
                results: finalResults,
                continuationToken: aggregatedContinuationToken,
                sortOrder: originalSearchOptions.Sort,
                unsupportedSearchParameters: uniqueUnsupportedParams);

            _logger.LogInformation(
                "Sequential aggregation completed. {ResultCount} results, continuation: {HasContinuation}",
                finalResults.Count,
                !string.IsNullOrEmpty(aggregatedContinuationToken));

            return Task.FromResult(aggregatedResult);
        }

        /// <inheritdoc />
        public Task<SearchResult> ProcessIncludesAsync(
            SearchResult primaryResults,
            SearchOptions searchOptions,
            CancellationToken cancellationToken)
        {
            // This is a placeholder for include/revinclude processing
            // In Phase 3, this will be implemented to:
            // 1. Extract references from primary results
            // 2. Create _id queries for referenced resources
            // 3. Execute parallel fanout queries to collect includes
            // 4. Merge include results back into the primary results

            _logger.LogInformation("Include processing not yet implemented - returning primary results unchanged");

            return Task.FromResult(primaryResults);
        }

        private static List<SearchResultEntry> SortResults(
            IEnumerable<SearchResultEntry> results,
            IReadOnlyList<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)> sortOrder)
        {
            if (sortOrder == null || !sortOrder.Any())
            {
                return results.ToList();
            }

            // For now, implement basic sorting by common parameters
            // In a complete implementation, this would need to handle all FHIR search parameter types
            var sorted = results.AsEnumerable();

            foreach (var sort in sortOrder.Reverse()) // Apply sorts in reverse order for stable sorting
            {
                var paramName = sort.searchParameterInfo.Name.ToLowerInvariant();
                var ascending = sort.sortOrder == SortOrder.Ascending;

                sorted = paramName switch
                {
                    "_lastmodified" => SortByLastModified(sorted, ascending),
                    "_id" => SortById(sorted, ascending),
                    _ => sorted, // Unsupported sort parameter, maintain current order
                };
            }

            return sorted.ToList();
        }

        private static IEnumerable<SearchResultEntry> SortByLastModified(IEnumerable<SearchResultEntry> results, bool ascending)
        {
            return ascending
                ? results.OrderBy(r => r.Resource.LastModified)
                : results.OrderByDescending(r => r.Resource.LastModified);
        }

        private static IEnumerable<SearchResultEntry> SortById(IEnumerable<SearchResultEntry> results, bool ascending)
        {
            return ascending
                ? results.OrderBy(r => r.Resource.ResourceId)
                : results.OrderByDescending(r => r.Resource.ResourceId);
        }

        private static string SerializeSortCriteria(IReadOnlyList<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)> sortOrder)
        {
            if (sortOrder == null || !sortOrder.Any())
            {
                return null;
            }

            var criteria = sortOrder.Select(s =>
                s.sortOrder == SortOrder.Ascending ? s.searchParameterInfo.Name : $"-{s.searchParameterInfo.Name}");

            return string.Join(",", criteria);
        }

        /// <summary>
        /// Ensures each search result entry has the correct fullUrl with source server base URL.
        /// This implements the ADR requirement for fullUrl differentiation using source server URLs.
        /// </summary>
        private IEnumerable<SearchResultEntry> EnsureServerFullUrls(IEnumerable<SearchResultEntry> results, string serverBaseUrl)
        {
            foreach (var result in results)
            {
                if (result.Resource?.Request?.Url != null)
                {
                    // Check if the fullUrl already contains the correct server base URL
                    var currentUrl = result.Resource.Request.Url.ToString();
                    if (!currentUrl.StartsWith(serverBaseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        // Update the ResourceRequest to have the correct server base URL
                        var correctedUri = new Uri($"{serverBaseUrl.TrimEnd('/')}/{result.Resource.ResourceTypeName}/{result.Resource.ResourceId}");
                        var correctedRequest = new Microsoft.Health.Fhir.Core.Features.Persistence.ResourceRequest(
                            result.Resource.Request.Method,
                            correctedUri);

                        // Create a new ResourceWrapper with the corrected request
                        var correctedWrapper = new Microsoft.Health.Fhir.Core.Features.Persistence.ResourceWrapper(
                            result.Resource.ResourceId,
                            result.Resource.Version,
                            result.Resource.ResourceTypeName,
                            result.Resource.RawResource,
                            correctedRequest,
                            result.Resource.LastModified,
                            result.Resource.IsDeleted,
                            result.Resource.SearchIndices,
                            result.Resource.CompartmentIndices,
                            result.Resource.LastModifiedClaims);

                        yield return new SearchResultEntry(correctedWrapper, result.SearchEntryMode);
                    }
                    else
                    {
                        yield return result;
                    }
                }
                else
                {
                    yield return result;
                }
            }
        }

        /// <summary>
        /// Extracts the last sort value from a search result for distributed sorting continuation.
        /// This enables proper resumption of sorted queries across multiple servers.
        /// </summary>
        private string ExtractLastSortValue(SearchResult searchResult, IReadOnlyList<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)> sortOrder)
        {
            if (searchResult?.Results == null || !searchResult.Results.Any() || sortOrder == null || !sortOrder.Any())
            {
                return null;
            }

            try
            {
                var resultsArray = searchResult.Results.ToArray();
                var lastResult = resultsArray[resultsArray.Length - 1];
                var primarySortParam = sortOrder[0];

                // Extract sort value based on the primary sort parameter
                string sortValue = primarySortParam.searchParameterInfo.Name.ToLowerInvariant() switch
                {
                    "_lastmodified" => lastResult.Resource.LastModified.ToString("o"), // ISO 8601 format
                    "_id" => lastResult.Resource.ResourceId,
                    "id" => lastResult.Resource.ResourceId,
                    _ => ExtractSortValueFromResource(lastResult.Resource, primarySortParam.searchParameterInfo.Name)
                };

                _logger.LogDebug("Extracted sort value '{SortValue}' for parameter '{SortParam}' from server result",
                    sortValue, primarySortParam.searchParameterInfo.Name);

                return sortValue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract sort value from search result");
                return null;
            }
        }

        /// <summary>
        /// Extracts a sort value from a resource's raw data for a specific sort parameter.
        /// </summary>
        private string ExtractSortValueFromResource(ResourceWrapper resource, string sortParameterName)
        {
            try
            {
                if (resource?.RawResource?.Data == null)
                {
                    return null;
                }

                // Parse the raw resource JSON to extract the sort field
                using var document = System.Text.Json.JsonDocument.Parse(resource.RawResource.Data);
                var root = document.RootElement;

                // Try to find the sort parameter in the resource
                if (root.TryGetProperty(sortParameterName, out var sortElement))
                {
                    return sortElement.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => sortElement.GetString(),
                        System.Text.Json.JsonValueKind.Number => sortElement.GetRawText(),
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        _ => sortElement.GetRawText()
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not extract sort value for parameter '{SortParam}' from resource", sortParameterName);
                return null;
            }
        }
    }
}
