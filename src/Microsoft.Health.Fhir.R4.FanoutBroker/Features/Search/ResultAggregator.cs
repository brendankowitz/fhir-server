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
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Aggregates search results from multiple FHIR servers into unified responses.
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
        public async Task<SearchResult> AggregateParallelResultsAsync(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions originalSearchOptions,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Aggregating parallel results from {ServerCount} servers", serverResults.Count);

            var allResults = new List<SearchResultEntry>();
            var allUnsupportedParams = new List<Tuple<string, string>>();
            var serverTokens = new List<ServerContinuationToken>();

            // Collect results from all servers
            foreach (var serverResult in serverResults)
            {
                if (serverResult.SearchResult?.Results != null)
                {
                    allResults.AddRange(serverResult.SearchResult.Results);
                }

                if (serverResult.SearchResult?.UnsupportedSearchParameters != null)
                {
                    allUnsupportedParams.AddRange(serverResult.SearchResult.UnsupportedSearchParameters);
                }

                // Create server continuation token
                var serverToken = new ServerContinuationToken
                {
                    Endpoint = serverResult.ServerId,
                    Token = serverResult.SearchResult?.ContinuationToken,
                    Exhausted = string.IsNullOrEmpty(serverResult.SearchResult?.ContinuationToken),
                    ResultsReturned = serverResult.SearchResult?.Results?.Count() ?? 0
                };

                serverTokens.Add(serverToken);
            }

            // Remove duplicate unsupported parameters
            var uniqueUnsupportedParams = allUnsupportedParams
                .Distinct()
                .ToList();

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
                    ResourceType = originalSearchOptions.ResourceType
                };

                aggregatedContinuationToken = fanoutToken.ToBase64String();
            }

            var aggregatedResult = new SearchResult(
                results: finalResults,
                continuationToken: aggregatedContinuationToken,
                sortOrder: originalSearchOptions.Sort,
                unsupportedSearchParameters: uniqueUnsupportedParams);

            _logger.LogInformation("Parallel aggregation completed. {ResultCount} results, continuation: {HasContinuation}",
                finalResults.Count, !string.IsNullOrEmpty(aggregatedContinuationToken));

            return aggregatedResult;
        }

        /// <inheritdoc />
        public async Task<SearchResult> AggregateSequentialResultsAsync(
            IReadOnlyList<ServerSearchResult> serverResults,
            SearchOptions originalSearchOptions,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Aggregating sequential results from {ServerCount} servers", serverResults.Count);

            var allResults = new List<SearchResultEntry>();
            var allUnsupportedParams = new List<Tuple<string, string>>();
            var serverTokens = new List<ServerContinuationToken>();
            var targetCount = originalSearchOptions.MaxItemCount;

            // Collect results from servers in order, respecting count limit
            foreach (var serverResult in serverResults)
            {
                if (serverResult.SearchResult?.Results != null)
                {
                    var serverResultsList = serverResult.SearchResult.Results.ToList();
                    
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

                // Create server continuation token
                var serverToken = new ServerContinuationToken
                {
                    Endpoint = serverResult.ServerId,
                    Token = serverResult.SearchResult?.ContinuationToken,
                    Exhausted = string.IsNullOrEmpty(serverResult.SearchResult?.ContinuationToken),
                    ResultsReturned = serverResult.SearchResult?.Results?.Count() ?? 0
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
                    ResourceType = originalSearchOptions.ResourceType
                };

                aggregatedContinuationToken = fanoutToken.ToBase64String();
            }

            var aggregatedResult = new SearchResult(
                results: finalResults,
                continuationToken: aggregatedContinuationToken,
                sortOrder: originalSearchOptions.Sort,
                unsupportedSearchParameters: uniqueUnsupportedParams);

            _logger.LogInformation("Sequential aggregation completed. {ResultCount} results, continuation: {HasContinuation}",
                finalResults.Count, !string.IsNullOrEmpty(aggregatedContinuationToken));

            return aggregatedResult;
        }

        /// <inheritdoc />
        public async Task<SearchResult> ProcessIncludesAsync(
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
            
            return primaryResults;
        }

        private List<SearchResultEntry> SortResults(
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
                    "name" => SortByName(sorted, ascending),
                    _ => sorted // Unsupported sort parameter, maintain current order
                };
            }

            return sorted.ToList();
        }

        private IEnumerable<SearchResultEntry> SortByLastModified(IEnumerable<SearchResultEntry> results, bool ascending)
        {
            return ascending
                ? results.OrderBy(r => r.Resource.Meta?.LastUpdated)
                : results.OrderByDescending(r => r.Resource.Meta?.LastUpdated);
        }

        private IEnumerable<SearchResultEntry> SortById(IEnumerable<SearchResultEntry> results, bool ascending)
        {
            return ascending
                ? results.OrderBy(r => r.Resource.Id)
                : results.OrderByDescending(r => r.Resource.Id);
        }

        private IEnumerable<SearchResultEntry> SortByName(IEnumerable<SearchResultEntry> results, bool ascending)
        {
            // This is a simplified implementation for demonstration
            // Real implementation would need to handle resource-specific name fields
            return ascending
                ? results.OrderBy(r => GetResourceName(r.Resource))
                : results.OrderByDescending(r => GetResourceName(r.Resource));
        }

        private string GetResourceName(Hl7.Fhir.Model.Resource resource)
        {
            // Simplified name extraction - in reality this would be resource-type specific
            return resource switch
            {
                Hl7.Fhir.Model.Patient patient => GetPatientName(patient),
                Hl7.Fhir.Model.Organization org => org.Name,
                Hl7.Fhir.Model.Practitioner practitioner => GetPractitionerName(practitioner),
                _ => resource.Id ?? string.Empty
            };
        }

        private string GetPatientName(Hl7.Fhir.Model.Patient patient)
        {
            var name = patient.Name?.FirstOrDefault();
            if (name != null)
            {
                var family = string.Join(" ", name.Family ?? Enumerable.Empty<string>());
                var given = string.Join(" ", name.Given ?? Enumerable.Empty<string>());
                return $"{family}, {given}".Trim(' ', ',');
            }
            return patient.Id ?? string.Empty;
        }

        private string GetPractitionerName(Hl7.Fhir.Model.Practitioner practitioner)
        {
            var name = practitioner.Name?.FirstOrDefault();
            if (name != null)
            {
                var family = string.Join(" ", name.Family ?? Enumerable.Empty<string>());
                var given = string.Join(" ", name.Given ?? Enumerable.Empty<string>());
                return $"{family}, {given}".Trim(' ', ',');
            }
            return practitioner.Id ?? string.Empty;
        }

        private string SerializeSortCriteria(IReadOnlyList<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)> sortOrder)
        {
            if (sortOrder == null || !sortOrder.Any())
            {
                return null;
            }

            var criteria = sortOrder.Select(s =>
                s.sortOrder == SortOrder.Ascending ? s.searchParameterInfo.Name : $"-{s.searchParameterInfo.Name}");

            return string.Join(",", criteria);
        }
    }
}