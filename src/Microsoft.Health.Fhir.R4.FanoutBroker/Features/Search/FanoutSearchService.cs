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
using Microsoft.Health.Fhir.FanoutBroker.Features.Protection;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors;
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
        private readonly IResourceProtectionService _resourceProtectionService;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<FanoutSearchService> _logger;

        public FanoutSearchService(
            IExecutionStrategyAnalyzer strategyAnalyzer,
            IFhirServerOrchestrator serverOrchestrator,
            IResultAggregator resultAggregator,
            ISearchOptionsFactory searchOptionsFactory,
            IChainedSearchProcessor chainedSearchProcessor,
            IIncludeProcessor includeProcessor,
            IResourceProtectionService resourceProtectionService,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<FanoutSearchService> logger)
        {
            _strategyAnalyzer = EnsureArg.IsNotNull(strategyAnalyzer, nameof(strategyAnalyzer));
            _serverOrchestrator = EnsureArg.IsNotNull(serverOrchestrator, nameof(serverOrchestrator));
            _resultAggregator = EnsureArg.IsNotNull(resultAggregator, nameof(resultAggregator));
            _searchOptionsFactory = EnsureArg.IsNotNull(searchOptionsFactory, nameof(searchOptionsFactory));
            _chainedSearchProcessor = EnsureArg.IsNotNull(chainedSearchProcessor, nameof(chainedSearchProcessor));
            _includeProcessor = EnsureArg.IsNotNull(includeProcessor, nameof(includeProcessor));
            _resourceProtectionService = EnsureArg.IsNotNull(resourceProtectionService, nameof(resourceProtectionService));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public async System.Threading.Tasks.Task<SearchResult> SearchAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken,
            bool isAsyncOperation = false,
            ResourceVersionType resourceVersionTypes = ResourceVersionType.Latest,
            bool onlyIds = false,
            bool isIncludesOperation = false)
        {
            // Create search options with processed parameters
            var searchOptions = _searchOptionsFactory.Create(
                resourceType,
                queryParameters,
                isAsyncOperation,
                resourceVersionTypes,
                onlyIds,
                isIncludesOperation);

            // Add resourceType hint to UnsupportedSearchParams for endpoint preservation
            if (!string.IsNullOrEmpty(resourceType))
            {
                // Create a new list including the resourceTypeHint
                var enhancedParams = new List<Tuple<string, string>>(searchOptions.UnsupportedSearchParams)
                {
                    Tuple.Create("resourceTypeHint", resourceType)
                };

                // Use internal setter to update UnsupportedSearchParams directly
                searchOptions.UnsupportedSearchParams = enhancedParams.AsReadOnly();
            }

            // Fanout broker only supports latest resource versions (read-only)
            if (resourceVersionTypes != ResourceVersionType.Latest)
            {
                throw new InvalidOperationException("Fanout broker only supports latest resource versions.");
            }

            _logger.LogInformation("Starting fanout search for resource type: {ResourceType} with {ParamCount} parameters",
                resourceType, queryParameters.Count);

            // Validate request against resource protection limits
            var protectionResult = await _resourceProtectionService.ValidateSearchRequestAsync(searchOptions, cancellationToken: cancellationToken);
            if (!protectionResult.IsAllowed)
            {
                _logger.LogWarning("Search request rejected by resource protection: {Reason}", protectionResult.RejectionReason);
                throw new InvalidOperationException(protectionResult.RejectionReason);
            }

            // Begin operation tracking for concurrency control
            var operationToken = await _resourceProtectionService.BeginSearchOperationAsync();
            if (operationToken == null)
            {
                throw new InvalidOperationException("Server is currently handling the maximum number of concurrent searches. Please try again later.");
            }

            try
            {
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

                if (!string.IsNullOrEmpty(resourceType))
                {
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
                }

                return await SearchInternalAsync(resourceType, searchOptions, processedQueryParameters, cancellationToken);
            }
            finally
            {
                // Always clean up the operation token
                _resourceProtectionService.EndSearchOperation(operationToken);
            }
        }

    // Removed legacy CreateBasicSearchOptions fallback – SearchOptionsFactory now fully registered.

        /// <inheritdoc />
        public async System.Threading.Tasks.Task<SearchResult> SearchAsync(SearchOptions searchOptions, CancellationToken cancellationToken)
        {
            var resourceType = GetResourceType(searchOptions);
            _logger.LogInformation("Starting fanout search for resource type: {ResourceType}", resourceType);

            // Check if this is an $includes operation
            if (searchOptions.OnlyIds && searchOptions.UnsupportedSearchParams.Any(p => p.Item1.Equals("includesCt", StringComparison.OrdinalIgnoreCase)))
            {
                // This is an $includes operation request
                return await _includeProcessor.ProcessIncludesOperationAsync(
                    resourceType,
                    searchOptions.UnsupportedSearchParams,
                    cancellationToken);
            }

            // Extract chained search and include parameters from Expression if available
            var expressionParameters = ExtractQueryParametersFromExpression(searchOptions);

            // Merge both extracted parameters and unsupported search parameters
            var processedQueryParameters = new List<Tuple<string, string>>();
            processedQueryParameters.AddRange(expressionParameters);
            processedQueryParameters.AddRange(searchOptions.UnsupportedSearchParams);

            _logger.LogDebug("Combined parameters: {ExpressionCount} from expression, {UnsupportedCount} from unsupported params, {TotalCount} total",
                expressionParameters.Count, searchOptions.UnsupportedSearchParams.Count, processedQueryParameters.Count);

            // Process chained search parameters if present
            var finalQueryParameters = processedQueryParameters;

            // Check for chained searches in the expression
            var chainSearchDetector = new ChainSearchDetectorVisitor();
            if (searchOptions.Expression != null)
            {
                searchOptions.Expression.AcceptVisitor(chainSearchDetector, null);

                if (chainSearchDetector.HasChainedSearch)
                {
                    try
                    {
                        finalQueryParameters = await _chainedSearchProcessor.ProcessChainedSearchAsync(
                            resourceType, processedQueryParameters, cancellationToken);

                        if (finalQueryParameters.Count != processedQueryParameters.Count)
                        {
                            _logger.LogInformation("Chained search processing modified query from {OriginalCount} to {ProcessedCount} parameters",
                                processedQueryParameters.Count, finalQueryParameters.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing chained search parameters");
                        throw;
                    }
                }
            }

            return await SearchInternalAsync(resourceType, searchOptions, finalQueryParameters, cancellationToken);
        }

        /// <summary>
        /// Internal search method that handles the actual fanout logic.
        /// </summary>
        private async System.Threading.Tasks.Task<SearchResult> SearchInternalAsync(string resourceType, SearchOptions searchOptions, IReadOnlyList<Tuple<string, string>> queryParameters, CancellationToken cancellationToken)
        {
            var resourceTypes = GetResourceTypes(searchOptions);

            // If we have multiple resource types from _type parameter, log them
            if (resourceTypes.Length > 1)
            {
                _logger.LogInformation("Multi-resource type search for types: {ResourceTypes}", string.Join(", ", resourceTypes));
            }

            // Determine execution strategy based on search options and expression analysis
            var strategy = _strategyAnalyzer.DetermineStrategy(searchOptions);

            _logger.LogInformation("Using {Strategy} execution strategy for fanout search", strategy);

            // Execute search based on strategy
            SearchResult result = strategy switch
            {
                ExecutionStrategy.Parallel => await ExecuteParallelSearchAsync(searchOptions, cancellationToken),
                ExecutionStrategy.Sequential => await ExecuteSequentialSearchAsync(searchOptions, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(null, strategy, "Unknown execution strategy"),
            };

            // Process include/revinclude parameters if present
            if (_includeProcessor.HasIncludeParameters(queryParameters))
            {
                result = await _includeProcessor.ProcessIncludesAsync(
                    resourceType,
                    queryParameters,
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
        private async System.Threading.Tasks.Task<SearchResult> ExecuteParallelSearchAsync(SearchOptions searchOptions, CancellationToken cancellationToken)
        {
            var enabledServers = _serverOrchestrator.GetEnabledServers();
            var searchTasks = new List<System.Threading.Tasks.Task<ServerSearchResult>>();

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
            var searchResults = await System.Threading.Tasks.Task.WhenAll(searchTasks);

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
        private async System.Threading.Tasks.Task<SearchResult> ExecuteSequentialSearchAsync(SearchOptions searchOptions, CancellationToken cancellationToken)
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
            // Extract query parameters from expression and merge with UnsupportedSearchParams
            var expressionParameters = ExtractQueryParametersFromExpression(originalOptions);
            var effectiveParams = new List<Tuple<string, string>>();
            effectiveParams.AddRange(expressionParameters);
            effectiveParams.AddRange(originalOptions.UnsupportedSearchParams);

            // Add server-specific continuation token as a query parameter if present
            if (!string.IsNullOrEmpty(serverContinuationToken))
            {
                // Remove any existing continuation token parameters
                effectiveParams.RemoveAll(p => string.Equals(p.Item1, "ct", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(p.Item1, "_continuation", StringComparison.OrdinalIgnoreCase));

                // Add the server-specific continuation token
                effectiveParams.Add(new Tuple<string, string>("ct", serverContinuationToken));

                _logger.LogDebug("Added server-specific continuation token for distributed sorting");
            }

            // Since SearchOptions constructor is internal, we need to create a new instance through the factory
            // and then manually copy over the relevant settings
            var serverOptions = _searchOptionsFactory.Create(
                GetResourceType(originalOptions),
                effectiveParams,
                false, // isAsyncOperation
                ResourceVersionType.Latest,
                originalOptions.OnlyIds);

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

        /// <summary>
        /// Extracts query parameters from the search expression tree.
        /// </summary>
        private static IReadOnlyList<Tuple<string, string>> ExtractQueryParametersFromExpression(SearchOptions searchOptions)
        {
            if (searchOptions.Expression == null)
            {
                return Array.Empty<Tuple<string, string>>();
            }

            // Get resource type hint for context-aware parameter extraction
            var resourceTypeHint = searchOptions.UnsupportedSearchParams?.FirstOrDefault(p => p.Item1 == "resourceTypeHint")?.Item2;

            var extractor = new ExpressionToQueryParameterExtractor(resourceTypeHint);
            searchOptions.Expression.AcceptVisitor(extractor, null);
            return extractor.QueryParameters;
        }

        private static string[] GetResourceTypes(SearchOptions options)
        {
            // Try to get resource type from UnsupportedSearchParams hints
            var resourceTypeHint = options.UnsupportedSearchParams?.FirstOrDefault(p => p.Item1 == "resourceTypeHint")?.Item2;
            if (!string.IsNullOrWhiteSpace(resourceTypeHint))
            {
                return new[] { resourceTypeHint };
            }

            // Check if there's a _type parameter that specifies the resource type(s)
            var typeParam = options.UnsupportedSearchParams?.FirstOrDefault(p => p.Item1 == "_type")?.Item2;
            if (!string.IsNullOrWhiteSpace(typeParam))
            {
                // _type can contain multiple resource types separated by commas
                return typeParam.Split(',', StringSplitOptions.RemoveEmptyEntries)
                               .Select(t => t.Trim())
                               .Where(t => !string.IsNullOrWhiteSpace(t))
                               .ToArray();
            }

            // For system searches, this will be empty array
            return Array.Empty<string>();
        }

        private static string GetResourceType(SearchOptions options)
        {
            var resourceTypes = GetResourceTypes(options);
            return resourceTypes.Length > 0 ? resourceTypes[0] : null;
        }
    }
}
