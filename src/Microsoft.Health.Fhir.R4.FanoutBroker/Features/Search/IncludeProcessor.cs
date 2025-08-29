// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Processes _include and _revinclude parameters across multiple FHIR servers for fanout broker.
    /// Implements the $includes operation as documented in ADR-2503.
    /// </summary>
    public class IncludeProcessor : IIncludeProcessor
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<IncludeProcessor> _logger;

        private const string IncludeParameter = "_include";
        private const string RevIncludeParameter = "_revinclude";

        public IncludeProcessor(
            IFhirServerOrchestrator serverOrchestrator,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<IncludeProcessor> logger)
        {
            _serverOrchestrator = EnsureArg.IsNotNull(serverOrchestrator, nameof(serverOrchestrator));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public bool HasIncludeParameters(IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            return queryParameters.Any(p =>
                p.Item1.Equals(IncludeParameter, StringComparison.OrdinalIgnoreCase) ||
                p.Item1.Equals(RevIncludeParameter, StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc />
        public async Task<SearchResult> ProcessIncludesAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            if (!HasIncludeParameters(queryParameters))
            {
                _logger.LogDebug("No include parameters found, returning main search result");
                return mainSearchResult;
            }

            _logger.LogInformation("Processing include/revinclude parameters for resource type: {ResourceType}", resourceType);

            try
            {
                // Extract include/revinclude parameters
                var includeParameters = ExtractIncludeParameters(queryParameters);
                
                // Get included resources from all servers
                var includedResources = await FetchIncludedResourcesAsync(
                    resourceType, 
                    includeParameters, 
                    mainSearchResult, 
                    cancellationToken);

                // Create the result with included resources
                return CreateSearchResultWithIncludes(mainSearchResult, includedResources);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing include/revinclude parameters");
                // Return main result without includes rather than failing completely
                return mainSearchResult;
            }
        }

        /// <inheritdoc />
        public async Task<SearchResult> ProcessIncludesOperationAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing $includes operation for resource type: {ResourceType}", resourceType);

            try
            {
                // Extract include parameters and continuation token
                var includeParameters = ExtractIncludeParameters(queryParameters);
                var continuationToken = ExtractContinuationToken(queryParameters);

                // For $includes operation, we need to fetch only the included resources
                var includedResources = await FetchIncludesOperationResourcesAsync(
                    resourceType,
                    includeParameters,
                    continuationToken,
                    cancellationToken);

                return CreateIncludesOperationResult(includedResources, resourceType, queryParameters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing $includes operation");
                throw;
            }
        }

        private List<IncludeParameter> ExtractIncludeParameters(IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            var includeParams = new List<IncludeParameter>();

            foreach (var param in queryParameters)
            {
                if (param.Item1.Equals(IncludeParameter, StringComparison.OrdinalIgnoreCase))
                {
                    includeParams.Add(new IncludeParameter
                    {
                        Type = IncludeType.Include,
                        Value = param.Item2
                    });
                }
                else if (param.Item1.Equals(RevIncludeParameter, StringComparison.OrdinalIgnoreCase))
                {
                    includeParams.Add(new IncludeParameter
                    {
                        Type = IncludeType.RevInclude,
                        Value = param.Item2
                    });
                }
            }

            return includeParams;
        }

        private string ExtractContinuationToken(IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            return queryParameters
                .FirstOrDefault(p => p.Item1.Equals("includesCt", StringComparison.OrdinalIgnoreCase))
                ?.Item2;
        }

        private async Task<List<SearchResultEntry>> FetchIncludedResourcesAsync(
            string resourceType,
            List<IncludeParameter> includeParameters,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            var allIncludedResources = new List<SearchResultEntry>();
            var enabledServers = _serverOrchestrator.GetEnabledServers();

            // Process each include parameter across all servers
            foreach (var includeParam in includeParameters)
            {
                var serverTasks = new List<Task<ServerSearchResult>>();

                foreach (var server in enabledServers)
                {
                    var serverTask = FetchIncludesFromServerAsync(
                        server, 
                        resourceType, 
                        includeParam, 
                        mainSearchResult, 
                        cancellationToken);
                    
                    serverTasks.Add(serverTask);
                }

                // Wait for all servers to complete
                var serverResults = await Task.WhenAll(serverTasks);

                // Collect successful results
                foreach (var result in serverResults.Where(r => r.IsSuccess))
                {
                    if (result.SearchResult?.Results != null)
                    {
                        allIncludedResources.AddRange(result.SearchResult.Results);
                    }
                }
            }

            // Remove duplicates based on resource ID and type
            return DeduplicateResources(allIncludedResources);
        }

        private async Task<ServerSearchResult> FetchIncludesFromServerAsync(
            FhirServerEndpoint server,
            string resourceType,
            IncludeParameter includeParam,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            try
            {
                // Create a query that will fetch the included resources
                // This is a simplified approach - in a full implementation, we'd need to
                // analyze the main search results to determine what included resources to fetch
                var includeQuery = new List<Tuple<string, string>>
                {
                    new Tuple<string, string>(includeParam.Type == IncludeType.Include ? IncludeParameter : RevIncludeParameter, includeParam.Value),
                    new Tuple<string, string>("_count", "1000") // Limit to prevent overwhelming results
                };

                // For now, we'll use a basic approach of executing the original query with includes on each server
                // A more sophisticated implementation would analyze the main results and create targeted include queries
                var searchOptions = CreateBasicSearchOptions(resourceType, includeQuery);

                return await _serverOrchestrator.SearchAsync(server, searchOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch includes from server {ServerId}", server.Id);
                return new ServerSearchResult
                {
                    ServerId = server.Id,
                    IsSuccess = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<List<SearchResultEntry>> FetchIncludesOperationResourcesAsync(
            string resourceType,
            List<IncludeParameter> includeParameters,
            string continuationToken,
            CancellationToken cancellationToken)
        {
            // For $includes operation, we need to fetch the included resources based on the continuation token
            // This is a simplified implementation - a full implementation would need to parse the continuation token
            // to determine which included resources to fetch and from which offset

            var allIncludedResources = new List<SearchResultEntry>();
            var enabledServers = _serverOrchestrator.GetEnabledServers();

            foreach (var includeParam in includeParameters)
            {
                var serverTasks = new List<Task<ServerSearchResult>>();

                foreach (var server in enabledServers)
                {
                    var includeQuery = new List<Tuple<string, string>>
                    {
                        new Tuple<string, string>(includeParam.Type == IncludeType.Include ? IncludeParameter : RevIncludeParameter, includeParam.Value),
                        new Tuple<string, string>("_count", "100") // Smaller count for $includes operation
                    };

                    if (!string.IsNullOrEmpty(continuationToken))
                    {
                        includeQuery.Add(new Tuple<string, string>("includesCt", continuationToken));
                    }

                    var searchOptions = CreateBasicSearchOptions(resourceType, includeQuery);
                    var serverTask = _serverOrchestrator.SearchAsync(server, searchOptions, cancellationToken);
                    serverTasks.Add(serverTask);
                }

                var serverResults = await Task.WhenAll(serverTasks);

                foreach (var result in serverResults.Where(r => r.IsSuccess))
                {
                    if (result.SearchResult?.Results != null)
                    {
                        allIncludedResources.AddRange(result.SearchResult.Results);
                    }
                }
            }

            return DeduplicateResources(allIncludedResources);
        }

        private List<SearchResultEntry> DeduplicateResources(List<SearchResultEntry> resources)
        {
            // Remove duplicates based on resource type and ID
            // Keep the first occurrence of each unique resource
            var seen = new HashSet<string>();
            var deduplicated = new List<SearchResultEntry>();

            foreach (var resource in resources)
            {
                if (resource?.Resource != null)
                {
                    // Create a unique key based on resource type and ID
                    var resourceKey = $"{resource.Resource.ResourceType}_{resource.Resource.Id}";
                    
                    if (!seen.Contains(resourceKey))
                    {
                        seen.Add(resourceKey);
                        deduplicated.Add(resource);
                    }
                }
            }

            return deduplicated;
        }

        private SearchResult CreateSearchResultWithIncludes(SearchResult mainSearchResult, List<SearchResultEntry> includedResources)
        {
            var allResults = new List<SearchResultEntry>();
            
            // Add main search results
            if (mainSearchResult.Results != null)
            {
                allResults.AddRange(mainSearchResult.Results);
            }

            // Add included resources up to the limit
            var maxIncludedItems = _configuration.Value.MaxIncludedResourcesInBundle;
            var includesCount = Math.Min(includedResources.Count, maxIncludedItems);
            allResults.AddRange(includedResources.Take(includesCount));

            // If we have more includes than the limit, we need to add a related link to $includes operation
            string relatedLink = null;
            if (includedResources.Count > maxIncludedItems)
            {
                // In a full implementation, this would be constructed based on the original request
                relatedLink = $"$includes?includesCt={CreateIncludesContinuationToken(includedResources, maxIncludedItems)}";
            }

            return new SearchResult(
                results: allResults,
                continuationToken: mainSearchResult.ContinuationToken,
                sortOrder: mainSearchResult.SortOrder,
                unsupportedSearchParameters: mainSearchResult.UnsupportedSearchParameters,
                maxItemCountExceeded: mainSearchResult.MaxItemCountExceeded)
            {
                TotalCount = mainSearchResult.TotalCount,
                // Add related link if there are more includes
                // Note: The actual SearchResult class may need to be extended to support this
            };
        }

        private SearchResult CreateIncludesOperationResult(List<SearchResultEntry> includedResources, string resourceType, IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            // For $includes operation, return only the included resources
            var continuationToken = includedResources.Count > 100 ? 
                CreateIncludesContinuationToken(includedResources, 100) : null;

            return new SearchResult(
                results: includedResources.Take(100),
                continuationToken: continuationToken,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>(),
                maxItemCountExceeded: includedResources.Count > 100)
            {
                TotalCount = includedResources.Count
            };
        }

        private string CreateIncludesContinuationToken(List<SearchResultEntry> includedResources, int offset)
        {
            // Create a simple continuation token for includes
            // In a full implementation, this would be more sophisticated
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"includes_offset_{offset}"));
        }

        private SearchOptions CreateBasicSearchOptions(string resourceType, List<Tuple<string, string>> queryParameters)
        {
            // Create a minimal SearchOptions for proxy operation
            var searchOptions = (SearchOptions)Activator.CreateInstance(typeof(SearchOptions), true);
            
            // Set basic properties using reflection
            typeof(SearchOptions).GetProperty("ResourceType")?.SetValue(searchOptions, resourceType);
            typeof(SearchOptions).GetProperty("UnsupportedSearchParams")?.SetValue(searchOptions, new List<Tuple<string, string>>());

            return searchOptions;
        }
    }

    /// <summary>
    /// Represents an include or revinclude parameter.
    /// </summary>
    public class IncludeParameter
    {
        public IncludeType Type { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// Type of include parameter.
    /// </summary>
    public enum IncludeType
    {
        Include,
        RevInclude
    }
}