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
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Fanout search service that orchestrates search queries across multiple FHIR servers.
    /// </summary>
    public class FanoutSearchService : ISearchService
    {
        private readonly IExecutionStrategyAnalyzer _strategyAnalyzer;
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly IResultAggregator _resultAggregator;
        private readonly ISearchOptionsFactory _searchOptionsFactory;
        private readonly IChainedSearchProcessor _chainedSearchProcessor;
        private readonly IIncludeProcessor _includeProcessor;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<FanoutSearchService> _logger;

        public FanoutSearchService(
            IExecutionStrategyAnalyzer strategyAnalyzer,
            IFhirServerOrchestrator serverOrchestrator,
            IResultAggregator resultAggregator,
            ISearchOptionsFactory searchOptionsFactory,
            IChainedSearchProcessor chainedSearchProcessor,
            IIncludeProcessor includeProcessor,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<FanoutSearchService> logger)
        {
            _strategyAnalyzer = EnsureArg.IsNotNull(strategyAnalyzer, nameof(strategyAnalyzer));
            _serverOrchestrator = EnsureArg.IsNotNull(serverOrchestrator, nameof(serverOrchestrator));
            _resultAggregator = EnsureArg.IsNotNull(resultAggregator, nameof(resultAggregator));
            _searchOptionsFactory = EnsureArg.IsNotNull(searchOptionsFactory, nameof(searchOptionsFactory));
            _chainedSearchProcessor = EnsureArg.IsNotNull(chainedSearchProcessor, nameof(chainedSearchProcessor));
            _includeProcessor = EnsureArg.IsNotNull(includeProcessor, nameof(includeProcessor));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public async Task<SearchResult> SearchAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken,
            bool isAsyncOperation = false,
            ResourceVersionType resourceVersionTypes = ResourceVersionType.Latest,
            bool onlyIds = false,
            bool isIncludesOperation = false)
        {
            // Fanout broker only supports latest resource versions (read-only)
            if (resourceVersionTypes != ResourceVersionType.Latest)
            {
                throw new InvalidOperationException("Fanout broker only supports latest resource versions.");
            }

            _logger.LogInformation("Starting fanout search for resource type: {ResourceType} with {ParamCount} parameters", 
                resourceType, queryParameters.Count);

            // If this is an $includes operation, handle it directly
            if (isIncludesOperation)
            {
                return await _includeProcessor.ProcessIncludesOperationAsync(
                    resourceType,
                    queryParameters,
                    cancellationToken);
            }

            // Process chained search parameters if present
            var processedQueryParameters = queryParameters;
            
            try
            {
                processedQueryParameters = await _chainedSearchProcessor.ProcessChainedSearchAsync(
                    resourceType, queryParameters, cancellationToken);
                
                if (processedQueryParameters.Count != queryParameters.Count)
                {
                    _logger.LogInformation("Chained search processing modified query from {OriginalCount} to {ProcessedCount} parameters", 
                        queryParameters.Count, processedQueryParameters.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing chained search parameters");
                throw;
            }

            // Create search options with processed parameters
            SearchOptions searchOptions;
            try
            {
                searchOptions = _searchOptionsFactory.Create(
                    resourceType, 
                    processedQueryParameters, 
                    isAsyncOperation, 
                    resourceVersionTypes, 
                    onlyIds, 
                    isIncludesOperation);
            }
            catch (NotImplementedException)
            {
                // Fallback for simplified fanout broker implementation
                // Create a basic search options that works as a proxy
                searchOptions = CreateBasicSearchOptions(resourceType, processedQueryParameters, onlyIds);
            }

            return await SearchAsync(searchOptions, cancellationToken);
        }

        /// <summary>
        /// Creates basic search options for proxy operation when full SearchOptionsFactory is not available.
        /// </summary>
        private SearchOptions CreateBasicSearchOptions(string resourceType, IReadOnlyList<Tuple<string, string>> queryParameters, bool onlyIds)
        {
            // Create a minimal SearchOptions for proxy operation
            // This is a workaround for the SearchOptions internal constructor limitation
            var searchOptions = (SearchOptions)Activator.CreateInstance(typeof(SearchOptions), true);
            
            // Set basic properties using reflection or public setters where available
            typeof(SearchOptions).GetProperty("ResourceType")?.SetValue(searchOptions, resourceType);
            typeof(SearchOptions).GetProperty("OnlyIds")?.SetValue(searchOptions, onlyIds);
            typeof(SearchOptions).GetProperty("UnsupportedSearchParams")?.SetValue(searchOptions, new List<Tuple<string, string>>());

            // Extract count parameter
            var countParam = queryParameters.FirstOrDefault(p => p.Item1.Equals("_count", StringComparison.OrdinalIgnoreCase));
            if (countParam != null && int.TryParse(countParam.Item2, out int count))
            {
                try
                {
                    typeof(SearchOptions).GetProperty("MaxItemCount")?.SetValue(searchOptions, Math.Min(count, 1000));
                }
                catch
                {
                    // MaxItemCount property may not be settable - use default
                }
            }
            
            return searchOptions;
        }

        /// <inheritdoc />
        public async Task<SearchResult> SearchAsync(SearchOptions searchOptions, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting fanout search for resource type: {ResourceType}", 
                searchOptions.ResourceType);

            // Check if this is an $includes operation
            if (searchOptions.OnlyIds && searchOptions.UnsupportedSearchParams.Any(p => p.Item1.Equals("includesCt", StringComparison.OrdinalIgnoreCase)))
            {
                // This is an $includes operation request
                return await _includeProcessor.ProcessIncludesOperationAsync(
                    searchOptions.ResourceType,
                    searchOptions.UnsupportedSearchParams,
                    cancellationToken);
            }

            // For the simplified fanout broker, we don't parse complex expressions
            // Instead, we work as a query proxy forwarding requests to downstream servers
            
            // Determine execution strategy based on basic search options
            var strategy = _strategyAnalyzer.DetermineStrategy(searchOptions);
            
            _logger.LogInformation("Using {Strategy} execution strategy for fanout search", strategy);

            // Execute search based on strategy
            SearchResult result = strategy switch
            {
                ExecutionStrategy.Parallel => await ExecuteParallelSearchAsync(searchOptions, cancellationToken),
                ExecutionStrategy.Sequential => await ExecuteSequentialSearchAsync(searchOptions, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown execution strategy")
            };

            // Process include/revinclude parameters if present
            if (_includeProcessor.HasIncludeParameters(searchOptions.UnsupportedSearchParams))
            {
                result = await _includeProcessor.ProcessIncludesAsync(
                    searchOptions.ResourceType,
                    searchOptions.UnsupportedSearchParams,
                    result,
                    cancellationToken);
            }

            _logger.LogInformation("Fanout search completed. Returned {Count} results", 
                result.Results?.Count() ?? 0);

            return result;
        }

        /// <summary>
        /// Executes search queries in parallel across all enabled FHIR servers.
        /// </summary>
        private async Task<SearchResult> ExecuteParallelSearchAsync(SearchOptions searchOptions, CancellationToken cancellationToken)
        {
            var enabledServers = _serverOrchestrator.GetEnabledServers();
            var searchTasks = new List<Task<ServerSearchResult>>();

            // Parse continuation token if present
            FanoutContinuationToken fanoutToken = null;
            if (!string.IsNullOrEmpty(searchOptions.ContinuationToken))
            {
                fanoutToken = FanoutContinuationToken.FromBase64String(searchOptions.ContinuationToken);
            }

            // Create search tasks for each server
            foreach (var server in enabledServers)
            {
                var serverToken = fanoutToken?.Servers?.FirstOrDefault(s => s.Endpoint == server.Id)?.Token;
                var serverSearchOptions = CreateServerSearchOptions(searchOptions, serverToken);
                
                var searchTask = _serverOrchestrator.SearchAsync(server, serverSearchOptions, cancellationToken);
                searchTasks.Add(searchTask);
            }

            // Wait for all searches to complete
            var searchResults = await Task.WhenAll(searchTasks);

            // Filter out failed results
            var successfulResults = searchResults.Where(r => r.IsSuccess).ToList();
            var failedResults = searchResults.Where(r => !r.IsSuccess).ToList();

            if (failedResults.Any())
            {
                _logger.LogWarning("Some servers failed during parallel search: {FailedServers}", 
                    string.Join(", ", failedResults.Select(r => r.ServerId)));
            }

            // Aggregate results
            return await _resultAggregator.AggregateParallelResultsAsync(
                successfulResults, 
                searchOptions, 
                cancellationToken);
        }

        /// <summary>
        /// Executes search queries sequentially until sufficient results are obtained.
        /// </summary>
        private async Task<SearchResult> ExecuteSequentialSearchAsync(SearchOptions searchOptions, CancellationToken cancellationToken)
        {
            var enabledServers = _serverOrchestrator.GetEnabledServers()
                .OrderByDescending(s => s.Priority)
                .ToList();

            var results = new List<ServerSearchResult>();
            var totalResults = 0;
            var targetCount = searchOptions.MaxItemCount;
            var fillFactorThreshold = (int)(targetCount * _configuration.Value.FillFactor);

            // Parse continuation token if present
            FanoutContinuationToken fanoutToken = null;
            if (!string.IsNullOrEmpty(searchOptions.ContinuationToken))
            {
                fanoutToken = FanoutContinuationToken.FromBase64String(searchOptions.ContinuationToken);
            }

            // Determine starting server index from continuation token
            var startIndex = DetermineStartingServerIndex(fanoutToken, enabledServers);

            for (int i = startIndex; i < enabledServers.Count; i++)
            {
                var server = enabledServers[i];
                var serverToken = fanoutToken?.Servers?.FirstOrDefault(s => s.Endpoint == server.Id)?.Token;
                var serverSearchOptions = CreateServerSearchOptions(searchOptions, serverToken);

                try
                {
                    var result = await _serverOrchestrator.SearchAsync(server, serverSearchOptions, cancellationToken);
                    
                    if (result.IsSuccess)
                    {
                        results.Add(result);
                        totalResults += result.SearchResult?.Results?.Count() ?? 0;

                        // Check if we have sufficient results (fill factor logic)
                        if (totalResults >= fillFactorThreshold)
                        {
                            _logger.LogInformation("Sequential search completed early. Got {TotalResults} results from {ServerCount} servers", 
                                totalResults, results.Count);
                            break;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Server {ServerId} failed during sequential search: {Error}", 
                            server.Id, result.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error searching server {ServerId} during sequential search", server.Id);
                }
            }

            // Aggregate results
            return await _resultAggregator.AggregateSequentialResultsAsync(
                results, 
                searchOptions, 
                cancellationToken);
        }

        private SearchOptions CreateServerSearchOptions(SearchOptions originalOptions, string serverContinuationToken)
        {
            // Since SearchOptions constructor is internal, we need to create a new instance through the factory
            // and then manually copy over the relevant settings
            var serverOptions = _searchOptionsFactory.Create(
                originalOptions.ResourceType,
                originalOptions.UnsupportedSearchParams,
                false, // isAsyncOperation
                ResourceVersionType.Latest,
                originalOptions.OnlyIds);

            // Manually set the continuation token for this server
            if (!string.IsNullOrEmpty(serverContinuationToken))
            {
                // Note: In a real implementation, you would need access to set the continuation token
                // This is a limitation of the current SearchOptions design
                _logger.LogWarning("Unable to set server-specific continuation token due to SearchOptions design limitations");
            }

            return serverOptions;
        }

        private int DetermineStartingServerIndex(FanoutContinuationToken fanoutToken, List<FhirServerEndpoint> servers)
        {
            if (fanoutToken?.Servers == null || !fanoutToken.Servers.Any())
                return 0;

            // Find the first server that isn't exhausted
            for (int i = 0; i < servers.Count; i++)
            {
                var serverToken = fanoutToken.Servers.FirstOrDefault(s => s.Endpoint == servers[i].Id);
                if (serverToken == null || !serverToken.Exhausted)
                    return i;
            }

            return servers.Count; // All servers exhausted
        }

        #region Unsupported Operations

        /// <inheritdoc />
        public Task<SearchResult> SearchCompartmentAsync(
            string compartmentType,
            string compartmentId,
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken,
            bool isAsyncOperation = false,
            bool useSmartCompartmentDefinition = false)
        {
            throw new NotSupportedException("Compartment search is not supported in the fanout broker service.");
        }

        /// <inheritdoc />
        public Task<SearchResult> SearchHistoryAsync(
            string resourceType,
            string resourceId,
            PartialDateTime at,
            PartialDateTime since,
            PartialDateTime before,
            int? count,
            string summary,
            string continuationToken,
            string sort,
            CancellationToken cancellationToken,
            bool isAsyncOperation = false)
        {
            throw new NotSupportedException("History search is not supported in the fanout broker service.");
        }

        /// <inheritdoc />
        public Task<SearchResult> SearchForReindexAsync(
            IReadOnlyList<Tuple<string, string>> queryParameters,
            string searchParameterHash,
            bool countOnly,
            CancellationToken cancellationToken,
            bool isAsyncOperation = false)
        {
            throw new NotSupportedException("Reindex search is not supported in the fanout broker service.");
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<(long StartId, long EndId)>> GetSurrogateIdRanges(
            string resourceType,
            long startId,
            long endId,
            int rangeSize,
            int numberOfRanges,
            bool up,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Surrogate ID ranges are not supported in the fanout broker service.");
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<string>> GetUsedResourceTypes(CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Getting used resource types is not supported in the fanout broker service.");
        }

        /// <inheritdoc />
        public Task<IEnumerable<string>> GetFeedRanges(CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Feed ranges are not supported in the fanout broker service.");
        }

        #endregion
    }
}