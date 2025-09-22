// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Implements distributed resolution strategy for chained searches and includes
    /// across multiple FHIR servers where resources may be distributed across shards.
    /// </summary>
    public class DistributedResolutionStrategy : IResolutionStrategy
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly ISearchOptionsFactory _searchOptionsFactory;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<DistributedResolutionStrategy> _logger;

        public DistributedResolutionStrategy(
            IFhirServerOrchestrator serverOrchestrator,
            ISearchOptionsFactory searchOptionsFactory,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<DistributedResolutionStrategy> logger)
        {
            _serverOrchestrator = EnsureArg.IsNotNull(serverOrchestrator, nameof(serverOrchestrator));
            _searchOptionsFactory = EnsureArg.IsNotNull(searchOptionsFactory, nameof(searchOptionsFactory));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Tuple<string, string>>> ProcessChainedSearchAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resourceType, nameof(resourceType));
            EnsureArg.IsNotNull(queryParameters, nameof(queryParameters));

            var processedParams = new List<Tuple<string, string>>(queryParameters);
            var chainedParams = DetectChainedParameters(queryParameters);

            if (!chainedParams.Any())
            {
                _logger.LogDebug("No chained parameters detected, returning original parameters");
                return processedParams;
            }

            _logger.LogInformation("Processing {Count} chained parameters using Distributed resolution strategy", chainedParams.Count);

            foreach (var chainParam in chainedParams)
            {
                try
                {
                    // Phase 1: Resolve chain references across ALL shards
                    var resolvedIds = await ResolveChainAcrossAllShards(chainParam, cancellationToken);

                    if (resolvedIds.Count == 0)
                    {
                        _logger.LogWarning("Chain resolution for '{ChainParam}' returned no results", chainParam.Item1);
                        // Keep the original parameter to maintain FHIR compliance
                        continue;
                    }

                    if (resolvedIds.Count > _configuration.Value.MaxDistributedReferenceIds)
                    {
                        _logger.LogWarning("Chain resolution for '{ChainParam}' returned {Count} results, exceeding limit of {Limit}. Truncating.",
                            chainParam.Item1, resolvedIds.Count, _configuration.Value.MaxDistributedReferenceIds);
                        resolvedIds = resolvedIds.Take(_configuration.Value.MaxDistributedReferenceIds).ToList();
                    }

                    // Phase 2: Replace chain parameter with resolved IDs
                    processedParams = ReplaceChainParameterWithIds(processedParams, chainParam, resolvedIds);

                    _logger.LogInformation("Successfully resolved chain '{ChainParam}' to {Count} resource IDs",
                        chainParam.Item1, resolvedIds.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing chained parameter '{ChainParam}'. Keeping original parameter.", chainParam.Item1);
                    // Keep the original parameter to allow potential fallback processing
                }
            }

            return processedParams;
        }

        /// <inheritdoc />
        public async Task<SearchResult> ProcessIncludesAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resourceType, nameof(resourceType));
            EnsureArg.IsNotNull(queryParameters, nameof(queryParameters));
            EnsureArg.IsNotNull(mainSearchResult, nameof(mainSearchResult));

            var includeParams = DetectIncludeParameters(queryParameters);

            if (!includeParams.Any())
            {
                _logger.LogDebug("No include parameters detected, returning main search result");
                return mainSearchResult;
            }

            _logger.LogInformation("Processing {Count} include parameters using Distributed resolution strategy", includeParams.Count);

            try
            {
                // Phase 1: Extract all reference IDs from main results
                var referencesByType = ExtractReferenceIds(mainSearchResult, includeParams);

                if (!referencesByType.Any())
                {
                    _logger.LogDebug("No references found to include, returning main search result");
                    return mainSearchResult;
                }

                // Phase 2: Resolve includes across ALL shards
                var includeResults = await ResolveIncludesAcrossAllShards(referencesByType, cancellationToken);

                // Phase 3: Merge results
                var mergedResult = MergeWithIncludeResults(mainSearchResult, includeResults);

                _logger.LogInformation("Successfully processed includes using Distributed strategy. Added {Count} included resources",
                    includeResults.Sum(r => r.Results?.Count() ?? 0));

                return mergedResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing includes using Distributed strategy. Returning main search result.");
                return mainSearchResult;
            }
        }

        /// <summary>
        /// Detects include/revinclude parameters in the query parameter list.
        /// </summary>
        private static IReadOnlyList<Tuple<string, string>> DetectIncludeParameters(IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            var includeParams = new List<Tuple<string, string>>();

            foreach (var param in queryParameters)
            {
                if (IsIncludeParameter(param.Item1))
                {
                    includeParams.Add(param);
                }
            }

            return includeParams;
        }

        /// <summary>
        /// Determines if a parameter name represents an include or revinclude.
        /// </summary>
        private static bool IsIncludeParameter(string parameterName)
        {
            return parameterName.Equals("_include", StringComparison.OrdinalIgnoreCase) ||
                   parameterName.Equals("_revinclude", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts reference IDs from main search results based on include parameters.
        /// </summary>
        private Dictionary<string, List<string>> ExtractReferenceIds(
            SearchResult mainSearchResult,
            IReadOnlyList<Tuple<string, string>> includeParams)
        {
            var referencesByType = new Dictionary<string, List<string>>();

            foreach (var includeParam in includeParams)
            {
                var includeInfo = ParseIncludeParameter(includeParam);
                if (includeInfo == null)
                {
                    _logger.LogWarning("Failed to parse include parameter '{IncludeParam}'", includeParam.Item1);
                    continue;
                }

                // Extract references from main results
                var references = ExtractReferencesFromResults(mainSearchResult, includeInfo);

                if (references.Any())
                {
                    if (!referencesByType.TryGetValue(includeInfo.TargetResourceType, out var existingList))
                    {
                        existingList = new List<string>();
                        referencesByType[includeInfo.TargetResourceType] = existingList;
                    }
                    existingList.AddRange(references);
                }
            }

            // Remove duplicates
            foreach (var kvp in referencesByType.ToList())
            {
                referencesByType[kvp.Key] = kvp.Value.Distinct().ToList();
            }

            return referencesByType;
        }

        /// <summary>
        /// Parses an include parameter to extract source resource type, reference parameter, and target resource type.
        /// </summary>
        private static IncludeInfo ParseIncludeParameter(Tuple<string, string> includeParam)
        {
            var isRevInclude = includeParam.Item1.Equals("_revinclude", StringComparison.OrdinalIgnoreCase);
            var includeValue = includeParam.Item2;

            // Format: "ResourceType:referenceParameter" or "ResourceType:referenceParameter:TargetResourceType"
            var parts = includeValue.Split(':');

            if (parts.Length < 2)
            {
                return null;
            }

            var sourceResourceType = parts[0];
            var referenceParameter = parts[1];
            var targetResourceType = parts.Length > 2 ? parts[2] : InferTargetResourceTypeFromReference(referenceParameter);

            return new IncludeInfo
            {
                IsRevInclude = isRevInclude,
                SourceResourceType = sourceResourceType,
                ReferenceParameter = referenceParameter,
                TargetResourceType = targetResourceType
            };
        }

        /// <summary>
        /// Infers target resource type from reference parameter name.
        /// </summary>
        private static string InferTargetResourceTypeFromReference(string referenceParameter)
        {
            return referenceParameter.ToLowerInvariant() switch
            {
                "subject" => "Patient",
                "patient" => "Patient",
                "practitioner" => "Practitioner",
                "organization" => "Organization",
                "location" => "Location",
                "encounter" => "Encounter",
                "device" => "Device",
                "medication" => "Medication",
                _ => "Resource" // Generic fallback
            };
        }

        /// <summary>
        /// Extracts reference IDs from search results for a specific include parameter.
        /// </summary>
        private List<string> ExtractReferencesFromResults(SearchResult searchResult, IncludeInfo includeInfo)
        {
            var references = new List<string>();

            if (searchResult.Results == null)
                return references;

            foreach (var resultEntry in searchResult.Results)
            {
                // This is a simplified extraction - in a real implementation we'd need to:
                // 1. Parse the FHIR resource from RawResource
                // 2. Extract the actual reference values from the specified reference parameter
                // 3. Handle different reference formats (relative, absolute, etc.)

                // For now, return placeholder references that demonstrate the concept
                var resourceId = ExtractResourceIdFromSearchResultEntry(resultEntry);
                if (!string.IsNullOrEmpty(resourceId))
                {
                    // Generate placeholder references - in real implementation this would come from parsing the resource
                    references.Add($"{includeInfo.TargetResourceType}/{resourceId}-ref");
                }
            }

            return references;
        }

        /// <summary>
        /// Resolves includes across all FHIR servers in parallel.
        /// </summary>
        private async Task<List<SearchResult>> ResolveIncludesAcrossAllShards(
            Dictionary<string, List<string>> referencesByType,
            CancellationToken cancellationToken)
        {
            var allIncludeResults = new List<SearchResult>();

            foreach (var kvp in referencesByType)
            {
                var resourceType = kvp.Key;
                var referenceIds = kvp.Value;

                if (referenceIds.Count > _configuration.Value.MaxDistributedReferenceIds)
                {
                    _logger.LogWarning("Include resolution for '{ResourceType}' has {Count} references, exceeding limit of {Limit}. Truncating.",
                        resourceType, referenceIds.Count, _configuration.Value.MaxDistributedReferenceIds);
                    referenceIds = referenceIds.Take(_configuration.Value.MaxDistributedReferenceIds).ToList();
                }

                // Split into batches to avoid URL length limits
                var batches = referenceIds.Chunk(_configuration.Value.DistributedBatchSize);

                foreach (var batch in batches)
                {
                    var batchQuery = new[] { Tuple.Create("_id", string.Join(",", batch)) };
                    var searchOptions = _searchOptionsFactory.Create(
                        resourceType,
                        batchQuery,
                        false,
                        ResourceVersionType.Latest,
                        false);

                    // Query ALL servers for this batch
                    var enabledServers = _serverOrchestrator.GetEnabledServers();

                    using var includeCts = new CancellationTokenSource(TimeSpan.FromSeconds(_configuration.Value.DistributedIncludeTimeoutSeconds));
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, includeCts.Token);

                    var searchTasks = enabledServers.Select(server =>
                        _serverOrchestrator.SearchAsync(server, searchOptions, combinedCts.Token));

                    try
                    {
                        var batchResults = await Task.WhenAll(searchTasks);
                        var successfulResults = batchResults.Where(r => r.IsSuccess).Select(r => r.SearchResult).ToList();
                        allIncludeResults.AddRange(successfulResults);
                    }
                    catch (OperationCanceledException) when (includeCts.Token.IsCancellationRequested)
                    {
                        _logger.LogWarning("Include resolution for '{ResourceType}' timed out after {Timeout} seconds",
                            resourceType, _configuration.Value.DistributedIncludeTimeoutSeconds);
                    }
                }
            }

            return allIncludeResults;
        }

        /// <summary>
        /// Merges main search results with include results.
        /// </summary>
        private static SearchResult MergeWithIncludeResults(SearchResult mainResult, List<SearchResult> includeResults)
        {
            var allResults = new List<SearchResultEntry>();

            // Add main results
            if (mainResult.Results != null)
            {
                allResults.AddRange(mainResult.Results);
            }

            // Add include results
            foreach (var includeResult in includeResults)
            {
                if (includeResult.Results != null)
                {
                    allResults.AddRange(includeResult.Results);
                }
            }

            return new SearchResult(allResults, mainResult.ContinuationToken, mainResult.SortOrder, mainResult.UnsupportedSearchParameters);
        }

        /// <summary>
        /// Detects chained search parameters in the query parameter list.
        /// </summary>
        private static IReadOnlyList<Tuple<string, string>> DetectChainedParameters(IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            var chainedParams = new List<Tuple<string, string>>();

            foreach (var param in queryParameters)
            {
                // Detect chained parameters (containing dots like "subject.name" or "subject:Patient.name")
                if (IsChainedParameter(param.Item1))
                {
                    chainedParams.Add(param);
                }
            }

            return chainedParams;
        }

        /// <summary>
        /// Determines if a parameter name represents a chained search.
        /// </summary>
        private static bool IsChainedParameter(string parameterName)
        {
            // Pattern: parameterName contains a dot (.) indicating a chain
            // Examples: "subject.name", "subject:Patient.name", "organization.name"
            return parameterName.Contains('.', StringComparison.Ordinal);
        }

        /// <summary>
        /// Resolves a chained parameter across all FHIR servers in parallel.
        /// </summary>
        private async Task<List<string>> ResolveChainAcrossAllShards(
            Tuple<string, string> chainParam,
            CancellationToken cancellationToken)
        {
            // Parse the chained parameter to extract target resource type and search parameter
            var chainInfo = ParseChainParameter(chainParam);

            if (chainInfo == null)
            {
                _logger.LogWarning("Failed to parse chained parameter '{ChainParam}'", chainParam.Item1);
                return new List<string>();
            }

            _logger.LogDebug("Resolving chain: Reference='{Reference}', Target='{Target}', Parameter='{Parameter}', Value='{Value}'",
                chainInfo.ReferenceParameter, chainInfo.TargetResourceType, chainInfo.TargetParameter, chainParam.Item2);

            // Create sub-query parameters for the target resource
            var subQueryParams = new[] { Tuple.Create(chainInfo.TargetParameter, chainParam.Item2) };

            // Create search options for the sub-query
            var searchOptions = _searchOptionsFactory.Create(
                chainInfo.TargetResourceType,
                subQueryParams,
                false, // isAsyncOperation
                ResourceVersionType.Latest,
                false); // onlyIds

            // Execute sub-query across ALL servers in parallel
            var enabledServers = _serverOrchestrator.GetEnabledServers();

            using var chainCts = new CancellationTokenSource(TimeSpan.FromSeconds(_configuration.Value.DistributedChainTimeoutSeconds));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, chainCts.Token);

            var searchTasks = enabledServers.Select(server =>
                _serverOrchestrator.SearchAsync(server, searchOptions, combinedCts.Token));

            try
            {
                var results = await Task.WhenAll(searchTasks);
                var successfulResults = results.Where(r => r.IsSuccess).ToList();

                if (!successfulResults.Any())
                {
                    _logger.LogWarning("No successful results from chain resolution for '{ChainParam}'", chainParam.Item1);
                    return new List<string>();
                }

                var resolvedIds = ExtractAllResourceIds(successfulResults);

                _logger.LogInformation("Chain resolution for '{ChainParam}' found {Count} IDs across {ServerCount} servers",
                    chainParam.Item1, resolvedIds.Count, successfulResults.Count);

                return resolvedIds;
            }
            catch (OperationCanceledException) when (chainCts.Token.IsCancellationRequested)
            {
                _logger.LogWarning("Chain resolution for '{ChainParam}' timed out after {Timeout} seconds",
                    chainParam.Item1, _configuration.Value.DistributedChainTimeoutSeconds);
                return new List<string>();
            }
        }

        /// <summary>
        /// Parses a chained parameter to extract reference parameter, target resource type, and target parameter.
        /// </summary>
        private static ChainInfo ParseChainParameter(Tuple<string, string> chainParam)
        {
            var parameterName = chainParam.Item1;

            // Pattern 1: "subject:Patient.name" -> reference="subject", targetType="Patient", targetParam="name"
            var typedChainMatch = Regex.Match(parameterName, @"^([^:]+):([^.]+)\.(.+)$");
            if (typedChainMatch.Success)
            {
                return new ChainInfo
                {
                    ReferenceParameter = typedChainMatch.Groups[1].Value,
                    TargetResourceType = typedChainMatch.Groups[2].Value,
                    TargetParameter = typedChainMatch.Groups[3].Value
                };
            }

            // Pattern 2: "subject.name" -> reference="subject", targetType needs to be inferred, targetParam="name"
            var simpleChainMatch = Regex.Match(parameterName, @"^([^.]+)\.(.+)$");
            if (simpleChainMatch.Success)
            {
                var referenceParam = simpleChainMatch.Groups[1].Value;
                var targetParam = simpleChainMatch.Groups[2].Value;

                // Infer target resource type based on common reference parameter names
                var targetResourceType = InferTargetResourceType(referenceParam, targetParam);

                return new ChainInfo
                {
                    ReferenceParameter = referenceParam,
                    TargetResourceType = targetResourceType,
                    TargetParameter = targetParam
                };
            }

            return null;
        }

        /// <summary>
        /// Infers the target resource type based on reference parameter name and target parameter.
        /// </summary>
        private static string InferTargetResourceType(string referenceParam, string targetParam)
        {
            // Common inference patterns for FHIR reference parameters
            return referenceParam.ToLowerInvariant() switch
            {
                "subject" when targetParam.StartsWith("name", StringComparison.OrdinalIgnoreCase) => "Patient",
                "patient" => "Patient",
                "practitioner" => "Practitioner",
                "organization" => "Organization",
                "location" => "Location",
                "encounter" => "Encounter",
                "device" => "Device",
                "medication" => "Medication",
                "observation" => "Observation",
                "condition" => "Condition",
                "procedure" => "Procedure",
                _ => "Patient" // Default fallback
            };
        }

        /// <summary>
        /// Extracts all resource IDs from successful search results.
        /// </summary>
        private static List<string> ExtractAllResourceIds(IEnumerable<ServerSearchResult> results)
        {
            var resourceIds = new HashSet<string>();

            foreach (var result in results)
            {
                if (result.SearchResult?.Results == null)
                    continue;

                foreach (var resource in result.SearchResult.Results)
                {
                    if (resource.Resource?.RawResource != null)
                    {
                        // Try to extract ID from the FHIR resource
                        var resourceId = ExtractResourceIdFromSearchResultEntry(resource);
                        if (!string.IsNullOrEmpty(resourceId))
                        {
                            resourceIds.Add(resourceId);
                        }
                    }
                }
            }

            return resourceIds.ToList();
        }

        /// <summary>
        /// Extracts the resource ID from a search result entry.
        /// </summary>
        private static string ExtractResourceIdFromSearchResultEntry(SearchResultEntry resultEntry)
        {
            // Try to get ID from the ResourceWrapper's search indices
            // This is a simplified approach - in a real implementation we'd need to parse the FHIR resource
            var resourceWrapper = resultEntry.Resource;

            // For now, return a placeholder that can be improved later
            // TODO: Implement proper FHIR resource ID extraction from RawResource
            return $"extracted-id-{resourceWrapper.GetHashCode()}";
        }

        /// <summary>
        /// Extracts the resource ID from a potential reference string.
        /// </summary>
        private static string ExtractResourceIdFromReference(string resourceType, string idOrReference)
        {
            // Handle full references like "Patient/123" -> "123"
            if (idOrReference.Contains('/', StringComparison.Ordinal))
            {
                var parts = idOrReference.Split('/');
                return parts.Length > 1 ? parts[^1] : idOrReference;
            }

            // Handle plain IDs
            return idOrReference;
        }

        /// <summary>
        /// Replaces a chained parameter with an ID-based filter using resolved resource IDs.
        /// </summary>
        private static List<Tuple<string, string>> ReplaceChainParameterWithIds(
            List<Tuple<string, string>> parameters,
            Tuple<string, string> chainParam,
            IReadOnlyList<string> resolvedIds)
        {
            var result = new List<Tuple<string, string>>(parameters);

            // Remove the original chained parameter
            result.RemoveAll(p => p.Item1 == chainParam.Item1 && p.Item2 == chainParam.Item2);

            // Extract the reference parameter name from the chain
            var referenceParam = ExtractReferenceParameterName(chainParam.Item1);

            // Add the resolved IDs as a comma-separated list
            if (resolvedIds.Count > 0)
            {
                var idList = string.Join(",", resolvedIds);
                result.Add(Tuple.Create(referenceParam, idList));
            }

            return result;
        }

        /// <summary>
        /// Extracts the reference parameter name from a chained parameter.
        /// </summary>
        private static string ExtractReferenceParameterName(string chainedParameterName)
        {
            // "subject:Patient.name" -> "subject"
            // "subject.name" -> "subject"
            var colonIndex = chainedParameterName.IndexOf(':', StringComparison.Ordinal);
            var dotIndex = chainedParameterName.IndexOf('.', StringComparison.Ordinal);

            if (colonIndex >= 0 && colonIndex < dotIndex)
            {
                return chainedParameterName.Substring(0, colonIndex);
            }
            else if (dotIndex >= 0)
            {
                return chainedParameterName.Substring(0, dotIndex);
            }

            return chainedParameterName;
        }

        /// <summary>
        /// Information extracted from parsing a chained parameter.
        /// </summary>
        private class ChainInfo
        {
            public string ReferenceParameter { get; set; }
            public string TargetResourceType { get; set; }
            public string TargetParameter { get; set; }
        }

        /// <summary>
        /// Information extracted from parsing an include parameter.
        /// </summary>
        private class IncludeInfo
        {
            public bool IsRevInclude { get; set; }
            public string SourceResourceType { get; set; }
            public string ReferenceParameter { get; set; }
            public string TargetResourceType { get; set; }
        }
    }
}