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
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search.ContinuationToken;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.Sorting
{
    /// <summary>
    /// Implementation of distributed sorting service for multi-server FHIR searches.
    /// </summary>
    public class DistributedSortingService : IDistributedSortingService
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<DistributedSortingService> _logger;

        public DistributedSortingService(
            IFhirServerOrchestrator serverOrchestrator,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<DistributedSortingService> logger)
        {
            _serverOrchestrator = serverOrchestrator ?? throw new ArgumentNullException(nameof(serverOrchestrator));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<SearchResult> ExecuteFirstPageSortedSearchAsync(
            IReadOnlyList<FhirServerEndpoint> servers,
            SearchOptions searchOptions,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Executing first page of sorted search across {ServerCount} servers", servers.Count);

            // Request N results from each server simultaneously
            var requestedCount = searchOptions.MaxItemCount ?? _configuration.Value.MaxResultsPerServer;
            var serverTasks = servers.Select(server =>
                _serverOrchestrator.SearchAsync(server, searchOptions, cancellationToken)
            ).ToArray();

            var serverResults = await Task.WhenAll(serverTasks);
            var successfulResults = serverResults.Where(r => r.IsSuccess).ToList();

            if (!successfulResults.Any())
            {
                _logger.LogWarning("No servers returned successful results for sorted search");
                return CreateEmptySearchResult();
            }

            _logger.LogDebug("Received results from {SuccessfulCount}/{TotalCount} servers",
                successfulResults.Count, servers.Count);

            // Merge and re-sort results globally
            var mergedResult = MergeAndSortResults(successfulResults, searchOptions, requestedCount);

            // Create distributed continuation token for next page
            var continuationToken = CreateContinuationToken(successfulResults, searchOptions, mergedResult.Results);
            if (continuationToken != null)
            {
                mergedResult.ContinuationToken = continuationToken.Serialize();
            }

            _logger.LogInformation("First page sorted search completed: {ResultCount} results, continuation: {HasContinuation}",
                mergedResult.Results.Count, !string.IsNullOrEmpty(mergedResult.ContinuationToken));

            return mergedResult;
        }

        /// <inheritdoc />
        public async Task<SearchResult> ExecuteSubsequentPageSortedSearchAsync(
            IReadOnlyList<FhirServerEndpoint> servers,
            SearchOptions searchOptions,
            DistributedContinuationToken distributedToken,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Executing subsequent page of sorted search across {ServerCount} servers", servers.Count);

            // Check if token has expired
            var tokenTimeout = TimeSpan.FromMinutes(30); // Configurable timeout
            if (distributedToken.IsExpired(tokenTimeout))
            {
                throw new InvalidOperationException("Continuation token has expired");
            }

            var requestedCount = distributedToken.PageSize;
            var serverTasks = new List<Task<ServerSearchResult>>();

            // Request next N results from each server using their respective continuation tokens
            foreach (var server in servers)
            {
                var serverToken = distributedToken.GetServerToken(server.Id);

                // Create modified search options with server-specific continuation token
                var modifiedSearchOptions = CreateSearchOptionsWithContinuationToken(searchOptions, serverToken);

                serverTasks.Add(_serverOrchestrator.SearchAsync(server, modifiedSearchOptions, cancellationToken));
            }

            var serverResults = await Task.WhenAll(serverTasks);
            var successfulResults = serverResults.Where(r => r.IsSuccess).ToList();

            if (!successfulResults.Any())
            {
                _logger.LogWarning("No servers returned successful results for subsequent sorted search page");
                return CreateEmptySearchResult();
            }

            _logger.LogDebug("Received subsequent page results from {SuccessfulCount}/{TotalCount} servers",
                successfulResults.Count, servers.Count);

            // Merge and re-sort results globally
            var mergedResult = MergeAndSortResults(successfulResults, searchOptions, requestedCount);

            // Update distributed continuation token
            var updatedToken = CreateContinuationToken(successfulResults, searchOptions, mergedResult.Results);
            if (updatedToken != null)
            {
                // Preserve the original sort criteria and page size
                updatedToken.SortCriteria = distributedToken.SortCriteria;
                updatedToken.PageSize = distributedToken.PageSize;
                mergedResult.ContinuationToken = updatedToken.Serialize();
            }

            _logger.LogInformation("Subsequent page sorted search completed: {ResultCount} results, continuation: {HasContinuation}",
                mergedResult.Results.Count, !string.IsNullOrEmpty(mergedResult.ContinuationToken));

            return mergedResult;
        }

        /// <inheritdoc />
        public SearchResult MergeAndSortResults(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions searchOptions,
            int requestedCount)
        {
            _logger.LogDebug("Merging and sorting results from {ServerCount} servers, requested count: {RequestedCount}",
                serverResults.Count, requestedCount);

            // Collect all results from all servers
            var allResults = new List<SearchResultEntry>();
            var totalCount = 0;

            foreach (var serverResult in serverResults)
            {
                if (serverResult.SearchResult?.Results != null)
                {
                    allResults.AddRange(serverResult.SearchResult.Results);

                    if (serverResult.SearchResult.TotalCount.HasValue)
                    {
                        totalCount += serverResult.SearchResult.TotalCount.Value;
                    }
                }
            }

            _logger.LogDebug("Collected {TotalResults} results before sorting and limiting", allResults.Count);

            // Apply global sorting if sort parameters are specified
            if (searchOptions.Sort?.Any() == true)
            {
                allResults = SortResultsGlobally(allResults, searchOptions.Sort);
                _logger.LogDebug("Applied global sorting with {SortCount} sort parameters", searchOptions.Sort.Count);
            }

            // Take only the requested number of results
            var finalResults = allResults.Take(requestedCount).ToList();

            _logger.LogDebug("Returning {FinalCount} results after sorting and limiting", finalResults.Count);

            // Create the merged search result
            var searchResult = new SearchResult(
                finalResults,
                continuationToken: null, // Will be set by caller
                sortOrder: searchOptions.Sort,
                unsupportedSearchParameters: new List<Tuple<string, string>>())
            {
                TotalCount = totalCount > 0 ? totalCount : (int?)null
            };

            return searchResult;
        }

        /// <inheritdoc />
        public DistributedContinuationToken CreateContinuationToken(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions searchOptions,
            IReadOnlyList<SearchResultEntry> returnedResults)
        {
            // Check if any server has more results (has continuation tokens)
            var serversWithContinuation = serverResults
                .Where(r => r.IsSuccess && !string.IsNullOrEmpty(r.SearchResult?.ContinuationToken))
                .ToList();

            if (!serversWithContinuation.Any())
            {
                _logger.LogDebug("No servers have continuation tokens, pagination complete");
                return null; // No more pages available
            }

            var distributedToken = new DistributedContinuationToken
            {
                PageSize = searchOptions.MaxItemCount ?? _configuration.Value.MaxResultsPerServer,
                SortCriteria = string.Join(",", searchOptions.Sort?.Select(s =>
                    $"{(s.sortOrder == SortOrder.Descending ? "-" : "")}{s.searchParameterInfo.Name}") ?? new string[0])
            };

            // Add server continuation tokens
            foreach (var serverResult in serverResults)
            {
                if (serverResult.IsSuccess)
                {
                    var serverToken = serverResult.SearchResult?.ContinuationToken ?? string.Empty;
                    distributedToken.UpdateServerToken(serverResult.ServerId, serverToken);
                }
            }

            // Extract last sort values from the returned results for proper continuation
            if (returnedResults.Any() && searchOptions.Sort?.Any() == true)
            {
                var lastResult = returnedResults.Last();
                foreach (var sortParam in searchOptions.Sort)
                {
                    var sortValue = ExtractSortValue(lastResult, sortParam.searchParameterInfo.Name);
                    if (sortValue != null)
                    {
                        distributedToken.LastSortValues[sortParam.searchParameterInfo.Name] = sortValue;
                    }
                }
            }

            _logger.LogDebug("Created distributed continuation token for {ServerCount} servers with sort criteria: {SortCriteria}",
                distributedToken.Servers.Count, distributedToken.SortCriteria);

            return distributedToken;
        }

        private List<SearchResultEntry> SortResultsGlobally(
            List<SearchResultEntry> results,
            IReadOnlyList<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)> sortParameters)
        {
            if (!sortParameters.Any())
            {
                return results;
            }

            // Apply sorting using LINQ OrderBy/ThenBy
            IOrderedEnumerable<SearchResultEntry> orderedResults = null;

            for (int i = 0; i < sortParameters.Count; i++)
            {
                var sortParam = sortParameters[i];

                if (i == 0)
                {
                    // First sort parameter
                    orderedResults = sortParam.sortOrder == SortOrder.Ascending
                        ? results.OrderBy(r => ExtractSortValue(r, sortParam.searchParameterInfo.Name))
                        : results.OrderByDescending(r => ExtractSortValue(r, sortParam.searchParameterInfo.Name));
                }
                else
                {
                    // Subsequent sort parameters
                    orderedResults = sortParam.sortOrder == SortOrder.Ascending
                        ? orderedResults.ThenBy(r => ExtractSortValue(r, sortParam.searchParameterInfo.Name))
                        : orderedResults.ThenByDescending(r => ExtractSortValue(r, sortParam.searchParameterInfo.Name));
                }
            }

            return orderedResults?.ToList() ?? results;
        }

        private object ExtractSortValue(SearchResultEntry entry, string parameterName)
        {
            try
            {
                // Extract sort value from resource based on parameter name
                // This is a simplified implementation - a real implementation would use
                // proper FHIR path evaluation based on the search parameter definition

                if (entry?.Resource?.RawResource?.Data == null)
                    return null;

                // Common sort parameters
                switch (parameterName.ToLowerInvariant())
                {
                    case "_lastmodified":
                    case "lastmodified":
                        return entry.Resource.LastModified;

                    case "_id":
                    case "id":
                        return entry.Resource.ResourceId;

                    default:
                        // For other parameters, try to extract from resource JSON
                        // This would need proper FHIRPath evaluation in a complete implementation
                        return parameterName; // Fallback to parameter name for consistent sorting
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract sort value for parameter {ParameterName}", parameterName);
                return null;
            }
        }

        private SearchOptions CreateSearchOptionsWithContinuationToken(SearchOptions originalOptions, string continuationToken)
        {
            // This is a simplified approach - in a real implementation, you'd need to properly
            // modify the SearchOptions to include the continuation token
            // The exact implementation depends on how the core FHIR server handles continuation tokens

            var modifiedUnsupportedParams = originalOptions.UnsupportedSearchParams?.ToList() ?? new List<Tuple<string, string>>();

            if (!string.IsNullOrEmpty(continuationToken))
            {
                // Remove existing continuation token parameter if present
                modifiedUnsupportedParams.RemoveAll(p => p.Item1 == "ct" || p.Item1 == "_continuation");

                // Add new continuation token
                modifiedUnsupportedParams.Add(Tuple.Create("ct", continuationToken));
            }

            // Create new SearchOptions with modified parameters
            // Note: This is a simplified approach - the actual implementation would need
            // to properly construct SearchOptions with the continuation token
            return new SearchOptions
            {
                // Copy all properties from original options
                MaxItemCount = originalOptions.MaxItemCount,
                Sort = originalOptions.Sort,
                UnsupportedSearchParams = modifiedUnsupportedParams.AsReadOnly()
                // ... copy other properties as needed
            };
        }

        private SearchResult CreateEmptySearchResult()
        {
            return new SearchResult(
                new List<SearchResultEntry>(),
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>());
        }
    }
}