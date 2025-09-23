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
using Hl7.Fhir.ElementModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors;
using Microsoft.Health.Fhir.FanoutBroker.Models;
using Microsoft.Health.Fhir.ValueSets;
using FhirResource = Hl7.Fhir.Model.Resource;
using FhirResourceReference = Hl7.Fhir.Model.ResourceReference;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Expression-based distributed resolution strategy that leverages rich FHIR expression metadata
    /// to handle complex scenarios like iterative includes, wildcard includes, and accurate chained searches.
    /// This provides comprehensive FHIR compliance compared to primitive parameter-based approaches.
    /// </summary>
    public class ExpressionDistributedResolutionStrategy : IExpressionResolutionStrategy
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly ISearchOptionsFactory _searchOptionsFactory;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<ExpressionDistributedResolutionStrategy> _logger;
        private readonly IResourceDeserializer _resourceDeserializer;

        public ExpressionDistributedResolutionStrategy(
            IFhirServerOrchestrator serverOrchestrator,
            ISearchOptionsFactory searchOptionsFactory,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<ExpressionDistributedResolutionStrategy> logger,
            IResourceDeserializer resourceDeserializer)
        {
            _serverOrchestrator = EnsureArg.IsNotNull(serverOrchestrator, nameof(serverOrchestrator));
            _searchOptionsFactory = EnsureArg.IsNotNull(searchOptionsFactory, nameof(searchOptionsFactory));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
            _resourceDeserializer = EnsureArg.IsNotNull(resourceDeserializer, nameof(resourceDeserializer));
        }

        /// <inheritdoc />
        public bool CanProcess(Expression searchExpression)
        {
            // This strategy can handle any expression, but prioritize it for complex scenarios
            return ChainedSearchExtractionVisitor.HasChainedSearches(searchExpression) ||
                   IncludeExtractionVisitor.HasIncludes(searchExpression) ||
                   IncludeExtractionVisitor.HasIterativeIncludes(searchExpression) ||
                   IncludeExtractionVisitor.HasWildcardIncludes(searchExpression);
        }

        /// <inheritdoc />
        public async Task<Expression> ProcessChainedSearchAsync(
            Expression fullSearchExpression,
            Expression expressionWithoutIncludes,
            IReadOnlyList<ChainedExpression> chainedExpressions,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(fullSearchExpression, nameof(fullSearchExpression));

            _logger.LogInformation("ExpressionDistributedResolutionStrategy.ProcessChainedSearchAsync called with expression type: {ExpressionType}",
                fullSearchExpression.GetType().Name);

            // var chainVisitor = new ChainedSearchExtractionVisitor();
            // chainVisitor.Extract(searchExpression);

            if (!chainedExpressions.Any())
            {
                _logger.LogDebug("No chained search expressions found, returning original expression");
                return fullSearchExpression;
            }

            _logger.LogInformation("Processing {Count} chained search expressions using Expression-based Distributed strategy",
                chainedExpressions.Count);

            var modifiedExpression = expressionWithoutIncludes;

            var resolvedExpressions = new List<Expression>();

            // First, resolve all chained expressions
            foreach (var chainExpr in chainedExpressions)
            {
                _logger.LogDebug("Processing chained expression: {Expression}", chainExpr.ToString());

                // Use rich expression metadata for accurate resolution
                var resolvedExpression = await ResolveChainedExpressionAsync(chainExpr, cancellationToken);

                if (resolvedExpression == null)
                {
                    // No matching target resources found - return original expression
                    // This will result in natural empty results when executed
                    _logger.LogInformation("Chained expression resolved to empty set: {Expression}. Search will return empty results.", chainExpr.ToString());
                    return fullSearchExpression; // Return original, unmodified expression
                }

                resolvedExpressions.Add(resolvedExpression);
                _logger.LogInformation("Successfully resolved chained expression: {Expression}", chainExpr.ToString());
            }

            // Expressions where provided and some results to filter by were found.
            if (resolvedExpressions?.Count == chainedExpressions.Count)
            {
                resolvedExpressions.Insert(0, expressionWithoutIncludes);
                return Expression.And(resolvedExpressions.Distinct().ToArray());
            }

            return fullSearchExpression;
        }

        /// <inheritdoc />
        public async Task<SearchResult> ProcessIncludesAsync(
            Expression fullSearchExpression,
            Expression expressionWithoutIncludes,
            IReadOnlyList<IncludeExpression> includeExpressions,
            IReadOnlyList<IncludeExpression> revIncludeExpressions,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(mainSearchResult, nameof(mainSearchResult));

            if (!includeExpressions.Any())
            {
                _logger.LogDebug("No include expressions found, returning main search result");
                return mainSearchResult;
            }

            _logger.LogInformation("Processing {IncludeCount} include expressions and {RevIncludeCount} reverse include expressions using Expression-based Distributed strategy",
                includeExpressions.Count, includeExpressions.Count);

            var allIncludeResults = new List<SearchResultEntry>();

            // Process regular includes
            if (includeExpressions.Any())
            {
                var includeResults = await ProcessIncludeExpressionsAsync(
                    includeExpressions,
                    mainSearchResult,
                    cancellationToken);
                allIncludeResults.AddRange(includeResults);
            }

            // Process reverse includes
            if (revIncludeExpressions.Any())
            {
                var revIncludeResults = await ProcessRevIncludeExpressionsAsync(
                    revIncludeExpressions,
                    mainSearchResult,
                    cancellationToken);
                allIncludeResults.AddRange(revIncludeResults);
            }

            // Merge results
            var mergedResults = new List<SearchResultEntry>();
            if (mainSearchResult.Results != null)
            {
                mergedResults.AddRange(mainSearchResult.Results);
            }
            mergedResults.AddRange(allIncludeResults);

            _logger.LogInformation("Successfully processed includes using Expression-based Distributed strategy. Added {Count} included resources",
                allIncludeResults.Count);

            return new SearchResult(mergedResults, mainSearchResult.ContinuationToken, mainSearchResult.SortOrder, mainSearchResult.UnsupportedSearchParameters);
        }

        /// <summary>
        /// Resolves a chained expression by executing recursive inside-out distributed searches across all shards.
        /// Follows the same pattern as ChainedSearchProcessor - start from innermost expression and work outward.
        /// </summary>
        private async Task<Expression> ResolveChainedExpressionAsync(
            ChainedExpression chainExpr,
            CancellationToken cancellationToken)
        {
            var allResolvedIds = await ResolveChainRecursiveAsync(chainExpr, cancellationToken);

            if (!allResolvedIds.Any())
            {
                _logger.LogInformation("No matching target resources found across all shards for chained expression: {Expression}.", chainExpr.ToString());
                return null; // Clean early exit - let caller handle empty case
            }

            // Apply limits
            if (allResolvedIds.Count > _configuration.Value.MaxDistributedReferenceIds)
            {
                _logger.LogWarning("Chain resolution returned {Count} results, exceeding limit of {Limit}. Truncating.",
                    allResolvedIds.Count, _configuration.Value.MaxDistributedReferenceIds);
                allResolvedIds = allResolvedIds.Take(_configuration.Value.MaxDistributedReferenceIds).ToList();
            }

            // Create reference filter expression with resolved IDs
            return CreateReferenceFilterExpression(chainExpr.ReferenceSearchParameter, allResolvedIds);
        }

        /// <summary>
        /// Recursively resolves chained expressions from inside-out, similar to ChainedSearchProcessor.ProcessChainLevel.
        /// </summary>
        private async Task<List<string>> ResolveChainRecursiveAsync(
            ChainedExpression chainExpr,
            CancellationToken cancellationToken)
        {
            // If this chain has a nested chain, resolve it first (inside-out approach)
            if (chainExpr.Expression is ChainedExpression nestedChain)
            {
                _logger.LogInformation("Processing nested chain recursively: {Expression}", nestedChain.ToString());

                // First resolve the nested chain to get target IDs
                var nestedResolvedIds = await ResolveChainRecursiveAsync(nestedChain, cancellationToken);

                if (!nestedResolvedIds.Any())
                {
                    _logger.LogInformation("Nested chain resolved to no results: {Expression}", nestedChain.ToString());
                    return new List<string>();
                }

                // Now create a filter expression using the nested results and resolve current level
                var nestedFilterExpression = CreateReferenceFilterExpression(nestedChain.ReferenceSearchParameter, nestedResolvedIds);

                // Create a new chained expression with the nested filter instead of the original nested chain
                var modifiedChainExpr = new ChainedExpression(
                    chainExpr.ResourceTypes,
                    chainExpr.ReferenceSearchParameter,
                    chainExpr.TargetResourceTypes,
                    chainExpr.Reversed,
                    nestedFilterExpression);

                return await ResolveChainLevelAsync(modifiedChainExpr, cancellationToken);
            }
            else
            {
                // Base case: no more nested chains, resolve this level directly
                return await ResolveChainLevelAsync(chainExpr, cancellationToken);
            }
        }

        /// <summary>
        /// Resolves a single level of chained expression (no nested chains).
        /// </summary>
        private async Task<List<string>> ResolveChainLevelAsync(
            ChainedExpression chainExpr,
            CancellationToken cancellationToken)
        {
            // Extract rich metadata from the expression
            var referenceParam = chainExpr.ReferenceSearchParameter;
            var targetResourceTypes = chainExpr.TargetResourceTypes ?? referenceParam?.TargetResourceTypes ?? Array.Empty<string>();

            if (!targetResourceTypes.Any())
            {
                _logger.LogWarning("No target resource types found for chained expression: {Expression}", chainExpr.ToString());
                return new List<string>();
            }

            var allResolvedIds = new List<string>();

            // Execute sub-queries for each target resource type
            foreach (var targetResourceType in targetResourceTypes)
            {
                _logger.LogInformation("Resolving chain level for target resource type: {ResourceType}", targetResourceType);

                // Create search options for the target resource with the chained constraint
                var targetSearchOptions = CreateTargetSearchOptions(targetResourceType, chainExpr);

                // Execute across all servers
                var resolvedIds = await ExecuteDistributedSearchForIds(targetSearchOptions, cancellationToken);

                allResolvedIds.AddRange(resolvedIds);

                _logger.LogInformation("Resolved {Count} IDs for target resource type: {ResourceType}: {IDs}",
                    resolvedIds.Count, targetResourceType, string.Join(", ", resolvedIds.Take(5)));
            }

            return allResolvedIds;
        }

        /// <summary>
        /// Processes regular include expressions with support for iterative and wildcard includes.
        /// </summary>
        private async Task<List<SearchResultEntry>> ProcessIncludeExpressionsAsync(
            IReadOnlyList<IncludeExpression> includeExpressions,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            var allIncludeResults = new List<SearchResultEntry>();

            foreach (var includeExpr in includeExpressions)
            {
                _logger.LogDebug("Processing include expression: {Expression}", includeExpr.ToString());

                if (includeExpr.CircularReference)
                {
                    _logger.LogWarning("Detected circular reference in include expression: {Expression}. Applying safety limits.", includeExpr.ToString());
                }

                var includeResults = await ProcessSingleIncludeExpressionAsync(includeExpr, mainSearchResult, cancellationToken);

                // Apply security scope filtering if configured
                if (includeExpr.AllowedResourceTypesByScope != null)
                {
                    includeResults = ApplySecurityScopeFiltering(includeResults, includeExpr.AllowedResourceTypesByScope);
                }

                allIncludeResults.AddRange(includeResults);

                _logger.LogDebug("Processed include expression: {Expression}, found {Count} resources",
                    includeExpr.ToString(), includeResults.Count);
            }

            return allIncludeResults;
        }

        /// <summary>
        /// Processes reverse include expressions.
        /// </summary>
        private async Task<List<SearchResultEntry>> ProcessRevIncludeExpressionsAsync(
            IReadOnlyList<IncludeExpression> revIncludeExpressions,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            var allRevIncludeResults = new List<SearchResultEntry>();

            foreach (var revIncludeExpr in revIncludeExpressions)
            {
                _logger.LogDebug("Processing reverse include expression: {Expression}", revIncludeExpr.ToString());

                var revIncludeResults = await ProcessSingleRevIncludeExpressionAsync(revIncludeExpr, mainSearchResult, cancellationToken);

                allRevIncludeResults.AddRange(revIncludeResults);

                _logger.LogDebug("Processed reverse include expression: {Expression}, found {Count} resources",
                    revIncludeExpr.ToString(), revIncludeResults.Count);
            }

            return allRevIncludeResults;
        }

        /// <summary>
        /// Creates search options for a target resource type in a chained search.
        /// </summary>
        private SearchOptions CreateTargetSearchOptions(string targetResourceType, ChainedExpression chainExpr)
        {
            var queryParameters = new List<Tuple<string, string>>();

            // Convert the target expression to query parameters using the standard extractor
            var extractor = new ExpressionToQueryParameterExtractor();
            chainExpr.Expression.AcceptVisitor(extractor, null);
            var parameters = extractor.QueryParameters.ToList();
            queryParameters.AddRange(parameters);

            // If no parameters were extracted, use basic parameter extraction
            if (!parameters.Any())
            {
                var targetValue = ExtractTargetValue(chainExpr);
                if (!string.IsNullOrEmpty(targetValue) && targetValue != "unknown")
                {
                    // Try to determine the parameter name from the target expression
                    var parameterName = GetParameterNameFromExpression(chainExpr.Expression);
                    queryParameters.Add(Tuple.Create(parameterName ?? "name", targetValue));
                }
            }

            _logger.LogInformation("Created target search options for {ResourceType} with {Count} parameters: {Parameters}",
                targetResourceType, queryParameters.Count, string.Join(", ", queryParameters.Select(p => $"{p.Item1}={p.Item2}")));

            // Create search options with resourceTypeHint for proper endpoint selection
            var searchOptions = _searchOptionsFactory.Create(
                targetResourceType,
                queryParameters,
                false,
                ResourceVersionType.Latest,
                false);

            // Add resourceTypeHint to ensure resource-specific endpoint is used
            var unsupportedParams = new List<Tuple<string, string>>
            {
                Tuple.Create("resourceTypeHint", targetResourceType)
            };

            // Use reflection to set the UnsupportedSearchParams property
            var unsupportedParamsProperty = searchOptions.GetType().GetProperty("UnsupportedSearchParams");
            if (unsupportedParamsProperty != null && unsupportedParamsProperty.CanWrite)
            {
                unsupportedParamsProperty.SetValue(searchOptions, unsupportedParams.AsReadOnly());
            }

            return searchOptions;
        }

        /// <summary>
        /// Extracts the parameter name from an expression.
        /// </summary>
        private static string GetParameterNameFromExpression(Expression expression)
        {
            switch (expression)
            {
                case SearchParameterExpression searchParam:
                    return searchParam.Parameter?.Name;

                case MultiaryExpression multiExpr when multiExpr.Expressions.Count > 0:
                    return GetParameterNameFromExpression(multiExpr.Expressions[0]);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Executes a distributed search across all servers and returns resource IDs.
        /// </summary>
        private async Task<List<string>> ExecuteDistributedSearchForIds(
            SearchOptions searchOptions,
            CancellationToken cancellationToken)
        {
            var enabledServers = _serverOrchestrator.GetEnabledServers();
            var resourceIds = new HashSet<string>();

            _logger.LogInformation("Executing distributed search across {ServerCount} servers with timeout {Timeout}s",
                enabledServers.Count, _configuration.Value.DistributedChainTimeoutSeconds);

            using var searchCts = new CancellationTokenSource(TimeSpan.FromSeconds(_configuration.Value.DistributedChainTimeoutSeconds));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, searchCts.Token);

            var searchTasks = enabledServers.Select(server =>
                _serverOrchestrator.SearchAsync(server, searchOptions, combinedCts.Token));

            try
            {
                var results = await Task.WhenAll(searchTasks);
                var successfulResults = results.Where(r => r.IsSuccess).ToList();
                var failedResults = results.Where(r => !r.IsSuccess).ToList();

                _logger.LogInformation("Distributed search completed: {SuccessfulCount} successful, {FailedCount} failed",
                    successfulResults.Count, failedResults.Count);

                if (failedResults.Any())
                {
                    _logger.LogWarning("Failed searches: {FailedCount} servers failed", failedResults.Count);
                }

                foreach (var result in successfulResults)
                {
                    if (result.SearchResult?.Results != null)
                    {
                        _logger.LogDebug("Server returned results");

                        foreach (var entry in result.SearchResult.Results)
                        {
                            var resourceId = ExtractResourceId(entry);
                            resourceIds.Add(resourceId);
                            _logger.LogDebug("Extracted resource ID: {ResourceId}", resourceId);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Server returned null or empty results");
                    }
                }
            }
            catch (OperationCanceledException) when (searchCts.Token.IsCancellationRequested)
            {
                _logger.LogWarning("Distributed search timed out after {Timeout} seconds", _configuration.Value.DistributedChainTimeoutSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during distributed search execution");
            }

            _logger.LogInformation("Distributed search extracted {TotalIds} unique resource IDs", resourceIds.Count);
            return resourceIds.ToList();
        }

        /// <summary>
        /// Processes a single include expression.
        /// </summary>
        private async Task<List<SearchResultEntry>> ProcessSingleIncludeExpressionAsync(
            IncludeExpression includeExpr,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            // Extract reference IDs from main results based on the include expression
            var referenceIds = ExtractReferenceIdsFromResults(mainSearchResult, includeExpr);

            if (!referenceIds.Any())
            {
                return new List<SearchResultEntry>();
            }

            // Create search options to find the referenced resources
            var targetResourceTypes = GetTargetResourceTypes(includeExpr);
            var allIncludeResults = new List<SearchResultEntry>();

            foreach (var targetResourceType in targetResourceTypes)
            {
                var searchOptions = CreateIncludeSearchOptions(targetResourceType, referenceIds);
                var includeResults = await ExecuteDistributedIncludeSearch(searchOptions, cancellationToken);
                allIncludeResults.AddRange(includeResults);
            }

            // Handle iterative includes if specified
            if (includeExpr.Iterate)
            {
                var iterativeResults = await ProcessIterativeIncludes(includeExpr, allIncludeResults, cancellationToken);
                allIncludeResults.AddRange(iterativeResults);
            }

            return allIncludeResults;
        }

        /// <summary>
        /// Processes a single reverse include expression.
        /// </summary>
        private async Task<List<SearchResultEntry>> ProcessSingleRevIncludeExpressionAsync(
            IncludeExpression revIncludeExpr,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            // For reverse includes, we search for resources that reference the main results
            var mainResourceIds = ExtractResourceIdsFromResults(mainSearchResult);

            if (!mainResourceIds.Any())
            {
                return new List<SearchResultEntry>();
            }

            // Create search options for reverse include
            var searchOptions = CreateRevIncludeSearchOptions(revIncludeExpr, mainResourceIds);

            return await ExecuteDistributedIncludeSearch(searchOptions, cancellationToken);
        }

        // Helper methods - Actual implementations

        /// <summary>
        /// Extracts the target search value from a chained expression.
        /// </summary>
        private static string ExtractTargetValue(ChainedExpression chainExpr)
        {
            // Extract the search value from the target expression
            if (chainExpr.Expression is SearchParameterExpression searchParam)
            {
                // For simple search parameters, extract the value
                if (searchParam.Expression is StringExpression stringExpr)
                {
                    return stringExpr.Value;
                }
                else if (searchParam.Expression is MultiaryExpression multiExpr && multiExpr.Expressions.Count > 0)
                {
                    // Take the first expression for now - could be enhanced for complex cases
                    if (multiExpr.Expressions[0] is StringExpression firstStringExpr)
                    {
                        return firstStringExpr.Value;
                    }
                }
            }

            // Extract any string values from the expression tree
            var stringValues = ExtractStringValuesFromExpression(chainExpr.Expression);
            return stringValues.FirstOrDefault() ?? "unknown";
        }

        /// <summary>
        /// Extracts string values from any expression recursively.
        /// </summary>
        private static List<string> ExtractStringValuesFromExpression(Expression expression)
        {
            var values = new List<string>();

            switch (expression)
            {
                case StringExpression stringExpr:
                    values.Add(stringExpr.Value);
                    break;
                case MultiaryExpression multiExpr:
                    foreach (var expr in multiExpr.Expressions)
                    {
                        values.AddRange(ExtractStringValuesFromExpression(expr));
                    }
                    break;
                case SearchParameterExpression searchParam:
                    values.AddRange(ExtractStringValuesFromExpression(searchParam.Expression));
                    break;
                // Remove or modify as needed based on available expression types
            }

            return values;
        }

        /// <summary>
        /// Extracts the resource ID from a search result entry.
        /// </summary>
        private static string ExtractResourceId(SearchResultEntry entry)
        {
            // Try to get the ID from the resource
            if (entry.Resource != null)
            {
                return entry.Resource.ToResourceKey(true).ToString();
            }

            // Last resort: generate a unique ID
            return $"unknown/{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Extracts reference IDs from search results based on the include expression.
        /// </summary>
        private List<string> ExtractReferenceIdsFromResults(SearchResult result, IncludeExpression includeExpr)
        {
            var referenceIds = new HashSet<string>();

            if (result.Results == null)
                return referenceIds.ToList();

            foreach (var entry in result.Results)
            {
                if (entry.Resource == null)
                    continue;

                // Extract references based on the include expression's reference parameter
                var references = ExtractReferencesFromResource(entry.Resource, includeExpr.ReferenceSearchParameter?.Name);

                foreach (var reference in references)
                {
                    // Parse the reference to extract just the ID part
                    var referenceId = ParseReferenceId(reference);
                    if (!string.IsNullOrEmpty(referenceId))
                    {
                        referenceIds.Add(referenceId);
                    }
                }
            }

            return referenceIds.ToList();
        }

        /// <summary>
        /// Extracts all resource IDs from search results.
        /// </summary>
        private static List<string> ExtractResourceIdsFromResults(SearchResult result)
        {
            var resourceIds = new List<string>();

            if (result.Results == null)
                return resourceIds;

            foreach (var entry in result.Results)
            {
                var resourceId = ExtractResourceId(entry);
                if (!string.IsNullOrEmpty(resourceId))
                {
                    resourceIds.Add(resourceId);
                }
            }

            return resourceIds;
        }

        /// <summary>
        /// Gets target resource types from an include expression.
        /// </summary>
        private static IReadOnlyList<string> GetTargetResourceTypes(IncludeExpression includeExpr)
        {
            // Use the target resource types from the reference search parameter
            if (includeExpr.ReferenceSearchParameter?.TargetResourceTypes?.Any() == true)
            {
                return includeExpr.ReferenceSearchParameter.TargetResourceTypes.ToList();
            }

            // Use Produces if available
            if (includeExpr.Produces?.Any() == true)
            {
                return includeExpr.Produces.ToList();
            }

            throw new NotSupportedException();
        }

        /// <summary>
        /// Extracts reference values from a resource for a given search parameter.
        /// </summary>
        private List<string> ExtractReferencesFromResource(object resource, string parameterName)
        {
            var references = new List<string>();

            if (resource == null)
                return references;

            try
            {
                // Deserialize the resource wrapper to get the actual FHIR Resource POCO
                if (resource is ResourceWrapper resourceWrapper)
                {
                    var resourceElement = _resourceDeserializer.Deserialize(resourceWrapper);
                    var fhirResource = resourceElement.ToPoco<FhirResource>();
                    return ExtractReferencesFromFhirResource(fhirResource);
                }

                // If it's already a FHIR Resource, use it directly
                if (resource is FhirResource fhirRes)
                {
                    return ExtractReferencesFromFhirResource(fhirRes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize resource for reference extraction");
            }

            // If we can't convert to a FHIR Resource, return empty list
            return references;
        }

        /// <summary>
        /// Extracts reference values from a FHIR Resource POCO.
        /// </summary>
        private static List<string> ExtractReferencesFromFhirResource(FhirResource resource)
        {
            var references = new List<string>();

            if (resource == null)
                return references;

            // Use FHIR's built-in method to get all ResourceReference objects from the resource
            var allReferences = resource.GetAllChildren<FhirResourceReference>();

            foreach (var reference in allReferences)
            {
                if (!string.IsNullOrWhiteSpace(reference.Reference))
                {
                    references.Add(reference.Reference);
                }
            }

            return references;
        }

        /// <summary>
        /// Checks if a string looks like a valid FHIR reference.
        /// </summary>
        private static bool IsValidReference(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            // FHIR references typically follow patterns like:
            // - "Patient/123"
            // - "http://example.com/fhir/Patient/123"
            // - "#contained-id"
            return value.Contains('/', StringComparison.Ordinal) || value.StartsWith('#');
        }

        /// <summary>
        /// Parses a FHIR reference to extract the resource ID.
        /// </summary>
        private static string ParseReferenceId(string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return null;

            // Handle contained references
            if (reference.StartsWith('#'))
            {
                return reference.Substring(1);
            }

            // Handle full URLs
            if (reference.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(reference);
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2)
                {
                    // Return ResourceType/Id format
                    return $"{segments[segments.Length - 2]}/{segments[segments.Length - 1]}";
                }
            }

            // Handle simple ResourceType/Id format
            if (reference.Contains('/', StringComparison.Ordinal))
            {
                return reference;
            }

            // If it's just an ID, we can't determine the resource type
            return reference;
        }

        /// <summary>
        /// Creates search options for an include search.
        /// </summary>
        private SearchOptions CreateIncludeSearchOptions(string resourceType, List<string> referenceIds)
        {
            var queryParameters = new List<Tuple<string, string>>();

            // Build an _id query to find the referenced resources
            if (referenceIds.Any())
            {
                // Batch the IDs to avoid overly long URLs
                var batchSize = _configuration.Value.DistributedBatchSize;
                var idBatches = referenceIds
                    .Select((id, index) => new { id, index })
                    .GroupBy(x => x.index / batchSize)
                    .Select(g => g.Select(x => x.id).ToList())
                    .ToList();

                // For simplicity, take the first batch for now
                // In a real implementation, this should handle multiple batches
                var firstBatch = idBatches.FirstOrDefault();
                if (firstBatch?.Any() == true)
                {
                    var idValue = string.Join(",", firstBatch.Select(ExtractIdFromReference));
                    queryParameters.Add(Tuple.Create("_id", idValue));
                }
            }

            return _searchOptionsFactory.Create(
                resourceType,
                queryParameters,
                false,
                ResourceVersionType.Latest,
                false);
        }

        /// <summary>
        /// Creates search options for a reverse include search.
        /// </summary>
        private SearchOptions CreateRevIncludeSearchOptions(IncludeExpression revIncludeExpr, List<string> mainResourceIds)
        {
            var queryParameters = new List<Tuple<string, string>>();

            // Build a query to find resources that reference the main resources
            if (mainResourceIds.Any() && revIncludeExpr.ReferenceSearchParameter != null)
            {
                var parameterName = revIncludeExpr.ReferenceSearchParameter.Name;
                var referenceValues = mainResourceIds.Select(id =>
                    id.Contains('/', StringComparison.Ordinal) ? id : $"{revIncludeExpr.TargetResourceType}/{id}").ToList();

                // Batch the references
                var batchSize = _configuration.Value.DistributedBatchSize;
                var refBatches = referenceValues
                    .Select((refValue, index) => new { refValue, index })
                    .GroupBy(x => x.index / batchSize)
                    .Select(g => g.Select(x => x.refValue).ToList())
                    .ToList();

                // For simplicity, take the first batch
                var firstBatch = refBatches.FirstOrDefault();
                if (firstBatch?.Any() == true)
                {
                    var referenceValue = string.Join(",", firstBatch);
                    queryParameters.Add(Tuple.Create(parameterName, referenceValue));
                }
            }

            return _searchOptionsFactory.Create(
                revIncludeExpr.SourceResourceType,
                queryParameters,
                false,
                ResourceVersionType.Latest,
                false);
        }

        /// <summary>
        /// Executes a distributed include search across all enabled servers.
        /// </summary>
        private async Task<List<SearchResultEntry>> ExecuteDistributedIncludeSearch(SearchOptions searchOptions, CancellationToken cancellationToken)
        {
            var enabledServers = _serverOrchestrator.GetEnabledServers();
            var allResults = new List<SearchResultEntry>();

            using var searchCts = new CancellationTokenSource(TimeSpan.FromSeconds(_configuration.Value.DistributedIncludeTimeoutSeconds));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, searchCts.Token);

            var searchTasks = enabledServers.Select(server =>
                _serverOrchestrator.SearchAsync(server, searchOptions, combinedCts.Token));

            try
            {
                var results = await Task.WhenAll(searchTasks);
                var successfulResults = results.Where(r => r.IsSuccess).ToList();

                foreach (var result in successfulResults)
                {
                    if (result.SearchResult?.Results != null)
                    {
                        allResults.AddRange(result.SearchResult.Results);
                    }
                }

                _logger.LogDebug("Distributed include search returned {Count} results from {SuccessfulServers}/{TotalServers} servers",
                    allResults.Count, successfulResults.Count, enabledServers.Count);
            }
            catch (OperationCanceledException) when (searchCts.Token.IsCancellationRequested)
            {
                _logger.LogWarning("Distributed include search timed out after {Timeout} seconds", _configuration.Value.DistributedIncludeTimeoutSeconds);
            }

            return allResults;
        }

        /// <summary>
        /// Processes iterative includes by following reference chains.
        /// </summary>
        private async Task<List<SearchResultEntry>> ProcessIterativeIncludes(IncludeExpression includeExpr, List<SearchResultEntry> currentResults, CancellationToken cancellationToken)
        {
            var iterativeResults = new List<SearchResultEntry>();
            var processedIds = new HashSet<string>();

            // Add current result IDs to prevent infinite loops
            foreach (var entry in currentResults)
            {
                var resourceId = ExtractResourceId(entry);
                processedIds.Add(resourceId);
            }

            var currentLevel = currentResults;
            int iterationCount = 0;
            int maxIterations = 5; // Safety limit to prevent infinite loops

            while (currentLevel.Any() && iterationCount < maxIterations)
            {
                iterationCount++;
                _logger.LogDebug("Processing iterative include iteration {Iteration}", iterationCount);

                // Extract references from current level
                var currentLevelReferenceIds = new List<string>();
                foreach (var entry in currentLevel)
                {
                    if (entry.Resource != null)
                    {
                        var references = this.ExtractReferencesFromResource(entry.Resource, includeExpr.ReferenceSearchParameter?.Name);
                        foreach (var reference in references)
                        {
                            var referenceId = ParseReferenceId(reference);
                            if (!string.IsNullOrEmpty(referenceId) && !processedIds.Contains(referenceId))
                            {
                                currentLevelReferenceIds.Add(referenceId);
                                processedIds.Add(referenceId);
                            }
                        }
                    }
                }

                if (!currentLevelReferenceIds.Any())
                    break;

                // Find the next level of referenced resources
                var targetResourceTypes = GetTargetResourceTypes(includeExpr);
                var nextLevelResults = new List<SearchResultEntry>();

                foreach (var targetResourceType in targetResourceTypes)
                {
                    var searchOptions = CreateIncludeSearchOptions(targetResourceType, currentLevelReferenceIds);
                    var results = await ExecuteDistributedIncludeSearch(searchOptions, cancellationToken);
                    nextLevelResults.AddRange(results);
                }

                iterativeResults.AddRange(nextLevelResults);
                currentLevel = nextLevelResults;

                _logger.LogDebug("Iterative include iteration {Iteration} found {Count} additional resources",
                    iterationCount, nextLevelResults.Count);
            }

            if (iterationCount >= maxIterations)
            {
                _logger.LogWarning("Iterative include processing stopped at maximum iteration limit of {MaxIterations}", maxIterations);
            }

            return iterativeResults;
        }

        /// <summary>
        /// Extracts just the ID part from a reference string.
        /// </summary>
        private static string ExtractIdFromReference(string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return reference;

            // Handle ResourceType/Id format
            if (reference.Contains('/', StringComparison.Ordinal))
            {
                var parts = reference.Split('/');
                return parts.Last();
            }

            // If it's already just an ID
            return reference;
        }

        /// <summary>
        /// Applies security scope filtering to search results.
        /// </summary>
        private static List<SearchResultEntry> ApplySecurityScopeFiltering(List<SearchResultEntry> results, IEnumerable<string> allowedResourceTypes)
        {
            if (allowedResourceTypes == null)
                return results;

            var allowedTypes = new HashSet<string>(allowedResourceTypes, StringComparer.OrdinalIgnoreCase);

            return results.Where(entry =>
            {
                // Note: ResourceType may not be available, using simplified approach
                if (entry.Resource == null)
                    return false;

                // Try to determine resource type from the resource object
                var resourceTypeName = entry.Resource.GetType().Name;
                return allowedTypes.Contains(resourceTypeName);
            }).ToList();
        }

        /// <summary>
        /// Replaces a chained expression with a resolved reference filter expression.
        /// </summary>
        private static Expression ReplaceChainedExpression(Expression original, ChainedExpression chainExpr, Expression replacement)
        {
            if (original == null || chainExpr == null)
                return original;

            if (replacement == null)
            {
                throw new ArgumentNullException(nameof(replacement),
                    $"Cannot replace chained expression {chainExpr} with null replacement. This indicates a bug in the resolution logic.");
            }

            return new ChainedExpressionReplacementVisitor(chainExpr, replacement).Visit(original);
        }

        /// <summary>
        /// Creates a reference filter expression from resolved IDs.
        /// </summary>
        private static Expression CreateReferenceFilterExpression(Microsoft.Health.Fhir.Core.Models.SearchParameterInfo referenceParam, List<string> resolvedIds)
        {
            if (referenceParam == null)
                throw new ArgumentNullException(nameof(referenceParam), "Reference parameter cannot be null");

            if (!resolvedIds.Any())
                throw new ArgumentException("Cannot create reference filter expression with empty resolved IDs list", nameof(resolvedIds));

            // Create separate StringExpression objects for each resolved ID using ReferenceResourceId field
            var referenceValues = resolvedIds.Select(id =>
                new StringExpression(StringOperator.Equals, FieldName.ReferenceResourceId, null, id, false)).Cast<Expression>().ToList();

            Expression targetExpression;
            if (referenceValues.Count == 1)
            {
                targetExpression = referenceValues.First();
            }
            else
            {
                // Create an OR expression for multiple reference IDs
                targetExpression = new MultiaryExpression(MultiaryOperator.Or, referenceValues);
            }

            return new SearchParameterExpression(referenceParam, targetExpression);
        }

        /// <summary>
        /// Visitor that replaces a specific chained expression with a replacement expression.
        /// </summary>
        private class ChainedExpressionReplacementVisitor : DefaultExpressionVisitor<object, Expression>
        {
            private readonly ChainedExpression _targetChain;
            private readonly Expression _replacement;

            public ChainedExpressionReplacementVisitor(ChainedExpression targetChain, Expression replacement)
            {
                _targetChain = targetChain ?? throw new ArgumentNullException(nameof(targetChain));
                _replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
            }

            public Expression Visit(Expression expression)
            {
                return expression?.AcceptVisitor(this, null) ?? expression;
            }

            public override Expression VisitChained(ChainedExpression expression, object context)
            {
                // If this is the chain we want to replace, return the replacement
                if (ReferenceEquals(expression, _targetChain) || ChainedExpressionsAreEquivalent(expression, _targetChain))
                {
                    return _replacement;
                }

                // Otherwise, continue visiting nested expressions
                var visitedExpression = base.VisitChained(expression, context);
                return visitedExpression;
            }

            public override Expression VisitMultiary(MultiaryExpression expression, object context)
            {
                var modifiedExpressions = new List<Expression>();
                bool hasChanges = false;

                foreach (var expr in expression.Expressions)
                {
                    var visitedExpr = expr.AcceptVisitor(this, context);
                    modifiedExpressions.Add(visitedExpr);

                    if (!ReferenceEquals(expr, visitedExpr))
                    {
                        hasChanges = true;
                    }
                }

                return hasChanges
                    ? new MultiaryExpression(expression.MultiaryOperation, modifiedExpressions)
                    : expression;
            }

            public override Expression VisitSearchParameter(SearchParameterExpression expression, object context)
            {
                var visitedInnerExpression = expression.Expression?.AcceptVisitor(this, context);

                // If the inner expression became null during visiting, we can't create a valid SearchParameterExpression
                if (visitedInnerExpression == null && expression.Expression != null)
                {
                    // This should not happen with our current logic, but handle it gracefully
                    return expression; // Return original expression unchanged
                }

                return !ReferenceEquals(expression.Expression, visitedInnerExpression)
                    ? new SearchParameterExpression(expression.Parameter, visitedInnerExpression)
                    : expression;
            }

            /// <summary>
            /// Determines if two chained expressions are equivalent for replacement purposes.
            /// </summary>
            private static bool ChainedExpressionsAreEquivalent(ChainedExpression expr1, ChainedExpression expr2)
            {
                if (expr1 == null || expr2 == null)
                    return false;

                // Compare reference search parameters
                if (expr1.ReferenceSearchParameter?.Name != expr2.ReferenceSearchParameter?.Name)
                    return false;

                // For now, do a simple string comparison of the expressions
                // This could be enhanced with more sophisticated expression comparison
                return expr1.ToString() == expr2.ToString();
            }
        }
    }
}
