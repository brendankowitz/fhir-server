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
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Processes _include and _revinclude parameters across multiple FHIR servers for fanout broker.
    /// Implements the $includes operation as documented in ADR-2503.
    /// </summary>
    public class IncludeProcessor : IIncludeProcessor
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly ISearchOptionsFactory _searchOptionsFactory;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<IncludeProcessor> _logger;

        private const string IncludeParameterName = "_include";
        private const string RevIncludeParameter = "_revinclude";

        public IncludeProcessor(
            IFhirServerOrchestrator serverOrchestrator,
            ISearchOptionsFactory searchOptionsFactory,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<IncludeProcessor> logger)
        {
            _serverOrchestrator = EnsureArg.IsNotNull(serverOrchestrator, nameof(serverOrchestrator));
            _searchOptionsFactory = EnsureArg.IsNotNull(searchOptionsFactory, nameof(searchOptionsFactory));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public bool HasIncludeParameters(IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            return queryParameters.Any(p =>
                p.Item1.Equals(IncludeParameterName, StringComparison.OrdinalIgnoreCase) ||
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
                if (param.Item1.Equals(IncludeParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    includeParams.Add(new IncludeParameter
                    {
                        Type = IncludeType.Include,
                        Value = param.Item2,
                    });
                }
                else if (param.Item1.Equals(RevIncludeParameter, StringComparison.OrdinalIgnoreCase))
                {
                    includeParams.Add(new IncludeParameter
                    {
                        Type = IncludeType.RevInclude,
                        Value = param.Item2,
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

            // Extract reference IDs from main search results to make targeted include queries
            var referenceIds = ExtractReferenceIds(mainSearchResult, includeParameters);

            if (!referenceIds.Any())
            {
                _logger.LogDebug("No reference IDs found in main search results for include processing");
                return allIncludedResources;
            }

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
                        referenceIds,
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
            Dictionary<string, HashSet<string>> referenceIds,
            CancellationToken cancellationToken)
        {
            try
            {
                // Parse the include parameter to determine target resource type and create targeted queries
                var (targetResourceType, targetQuery) = CreateTargetedIncludeQuery(includeParam, referenceIds);

                if (targetQuery == null || !targetQuery.Any())
                {
                    _logger.LogDebug("No targeted query could be created for include parameter {Include} on server {ServerId}",
                        includeParam.Value, server.Id);
                    return new ServerSearchResult
                    {
                        ServerId = server.Id,
                        IsSuccess = true,
                        SearchResult = new SearchResult(new List<SearchResultEntry>(), null, null, new List<Tuple<string, string>>(), null)
                    };
                }

                // Create search options for the targeted include query
                var searchOptions = _searchOptionsFactory.Create(targetResourceType, targetQuery);

                return await _serverOrchestrator.SearchAsync(server, searchOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch includes from server {ServerId}", server.Id);
                return new ServerSearchResult
                {
                    ServerId = server.Id,
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                };
            }
        }

        private async Task<List<SearchResultEntry>> FetchIncludesOperationResourcesAsync(
            string resourceType,
            List<IncludeParameter> includeParameters,
            string continuationToken,
            CancellationToken cancellationToken)
        {
            var allIncludedResources = new List<SearchResultEntry>();
            var enabledServers = _serverOrchestrator.GetEnabledServers();

            // Parse continuation token to determine offset and server distribution
            var (offset, serverStates) = ParseIncludesContinuationToken(continuationToken);
            var pageSize = 50; // Smaller page size for $includes operation

            foreach (var includeParam in includeParameters)
            {
                var serverTasks = new List<Task<ServerSearchResult>>();

                foreach (var server in enabledServers)
                {
                    var includeQuery = new List<Tuple<string, string>>
                    {
                        new Tuple<string, string>(includeParam.Type == IncludeType.Include ? IncludeParameterName : RevIncludeParameter, includeParam.Value),
                        new Tuple<string, string>("_count", pageSize.ToString()),
                    };

                    // Apply server-specific continuation if available
                    if (serverStates?.ContainsKey(server.Id) == true)
                    {
                        includeQuery.Add(new Tuple<string, string>("ct", serverStates[server.Id]));
                    }

                    var searchOptions = _searchOptionsFactory.Create(resourceType, includeQuery);
                    var serverTask = _serverOrchestrator.SearchAsync(server, searchOptions, cancellationToken);
                    serverTasks.Add(serverTask);
                }

                var serverResults = await Task.WhenAll(serverTasks);

                foreach (var result in serverResults.Where(r => r.IsSuccess))
                {
                    if (result.SearchResult?.Results != null)
                    {
                        // Apply fullUrl differentiation for included resources
                        var server = enabledServers.First(s => s.Id == result.ServerId);
                        var correctedResults = EnsureServerFullUrls(result.SearchResult.Results, server.BaseUrl);
                        allIncludedResources.AddRange(correctedResults);
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
                if (resource.Resource != null)
                {
                    // Use the wrapper properties (ResourceTypeName/ResourceId)
                    var resourceKey = $"{resource.Resource.ResourceTypeName}_{resource.Resource.ResourceId}";
                    // HashSet.Add already returns false if exists; no need for Contains (CA1868)
                    if (seen.Add(resourceKey))
                    {
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

            // NOTE: SearchResult does not currently expose a related link collection or MaxItemCountExceeded parameter.
            // We pass through original unsupported parameters and continuation token.
            var result = new SearchResult(
                allResults,
                mainSearchResult.ContinuationToken,
                mainSearchResult.SortOrder,
                mainSearchResult.UnsupportedSearchParameters,
                mainSearchResult.SearchIssues,
                includesContinuationToken: relatedLink);
            result.TotalCount = mainSearchResult.TotalCount;
            return result;
        }

        private SearchResult CreateIncludesOperationResult(List<SearchResultEntry> includedResources, string resourceType, IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            var pageSize = 50; // Consistent page size for $includes operation
            var continuationToken = includedResources.Count > pageSize ?
                CreateIncludesContinuationToken(includedResources, pageSize) : null;

            var limited = includedResources.Take(pageSize).ToList();

            // Sort results by resource type and ID for consistent ordering across servers
            limited = limited.OrderBy(r => r.Resource.ResourceTypeName)
                            .ThenBy(r => r.Resource.ResourceId)
                            .ToList();

            var includesResult = new SearchResult(
                limited,
                continuationToken,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>(),
                searchIssues: null,
                includesContinuationToken: continuationToken);
            includesResult.TotalCount = includedResources.Count;
            return includesResult;
        }

        private string CreateIncludesContinuationToken(List<SearchResultEntry> includedResources, int offset)
        {
            // Create a distributed continuation token that tracks state across servers
            var tokenData = new
            {
                Offset = offset,
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                ServerStates = new Dictionary<string, string>() // Will be populated by server-specific tokens if available
            };

            var json = System.Text.Json.JsonSerializer.Serialize(tokenData);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        }

        private (int offset, Dictionary<string, string> serverStates) ParseIncludesContinuationToken(string continuationToken)
        {
            if (string.IsNullOrEmpty(continuationToken))
            {
                return (0, null);
            }

            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken));
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var offset = root.TryGetProperty("Offset", out var offsetProp) ? offsetProp.GetInt32() : 0;
                var serverStates = new Dictionary<string, string>();

                if (root.TryGetProperty("ServerStates", out var statesProp))
                {
                    foreach (var prop in statesProp.EnumerateObject())
                    {
                        serverStates[prop.Name] = prop.Value.GetString();
                    }
                }

                return (offset, serverStates);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse includes continuation token, starting from beginning");
                return (0, null);
            }
        }

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
        /// Extracts reference IDs from the main search results to enable targeted include queries.
        /// </summary>
        private Dictionary<string, HashSet<string>> ExtractReferenceIds(SearchResult mainSearchResult, List<IncludeParameter> includeParameters)
        {
            var referenceIds = new Dictionary<string, HashSet<string>>();

            if (mainSearchResult?.Results == null)
            {
                return referenceIds;
            }

            foreach (var entry in mainSearchResult.Results)
            {
                try
                {
                    // Parse the raw resource to extract reference fields
                    if (entry.Resource?.RawResource?.Data != null)
                    {
                        var rawData = entry.Resource.RawResource.Data;
                        // This is a simplified extraction - in a full implementation, you would
                        // parse the JSON and extract specific reference fields based on the include parameters
                        ExtractReferenceIdsFromRawResource(rawData, referenceIds, includeParameters);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error extracting reference IDs from resource {ResourceType}/{ResourceId}",
                        entry.Resource?.ResourceTypeName, entry.Resource?.ResourceId);
                }
            }

            return referenceIds;
        }

        /// <summary>
        /// Extracts reference IDs from raw resource data based on include parameters.
        /// </summary>
        private void ExtractReferenceIdsFromRawResource(string rawData, Dictionary<string, HashSet<string>> referenceIds, List<IncludeParameter> includeParameters)
        {
            try
            {
                // Parse JSON to extract reference fields
                using var document = System.Text.Json.JsonDocument.Parse(rawData);
                var root = document.RootElement;

                foreach (var includeParam in includeParameters)
                {
                    // Parse include parameter: ResourceType:field or ResourceType:field:targetType
                    var parts = includeParam.Value.Split(':');
                    if (parts.Length >= 2)
                    {
                        var fieldName = parts[1];

                        // Extract reference values from the specified field
                        if (root.TryGetProperty(fieldName, out var referenceElement))
                        {
                            ExtractReferenceFromElement(referenceElement, fieldName, referenceIds);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not parse resource JSON for reference extraction");
            }
        }

        /// <summary>
        /// Extracts reference values from a JSON element (could be single reference or array).
        /// </summary>
        private void ExtractReferenceFromElement(System.Text.Json.JsonElement element, string fieldName, Dictionary<string, HashSet<string>> referenceIds)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    ExtractSingleReference(item, fieldName, referenceIds);
                }
            }
            else
            {
                ExtractSingleReference(element, fieldName, referenceIds);
            }
        }

        /// <summary>
        /// Extracts a single reference value from a JSON element.
        /// </summary>
        private void ExtractSingleReference(System.Text.Json.JsonElement element, string fieldName, Dictionary<string, HashSet<string>> referenceIds)
        {
            if (element.TryGetProperty("reference", out var referenceProperty) && referenceProperty.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var referenceValue = referenceProperty.GetString();
                if (!string.IsNullOrEmpty(referenceValue))
                {
                    // Parse reference format: ResourceType/id
                    var referenceParts = referenceValue.Split('/');
                    if (referenceParts.Length == 2)
                    {
                        var resourceType = referenceParts[0];
                        var resourceId = referenceParts[1];

                        if (!referenceIds.TryGetValue(resourceType, out var idSet))
                        {
                            idSet = new HashSet<string>();
                            referenceIds[resourceType] = idSet;
                        }
                        idSet.Add(resourceId);
                    }
                }
            }
        }

        /// <summary>
        /// Creates a targeted include query based on the include parameter and extracted reference IDs.
        /// </summary>
        private (string targetResourceType, List<Tuple<string, string>> targetQuery) CreateTargetedIncludeQuery(
            IncludeParameter includeParam,
            Dictionary<string, HashSet<string>> referenceIds)
        {
            // Parse include parameter: ResourceType:field or ResourceType:field:targetType
            var parts = includeParam.Value.Split(':');
            if (parts.Length < 2)
            {
                return (null, null);
            }

            var targetResourceType = parts.Length >= 3 ? parts[2] : GuessTargetResourceType(parts[1]);

            if (!referenceIds.TryGetValue(targetResourceType, out var idSet) || !idSet.Any())
            {
                return (targetResourceType, new List<Tuple<string, string>>());
            }

            var ids = string.Join(",", idSet);
            var targetQuery = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("_id", ids),
                new Tuple<string, string>("_count", Math.Min(idSet.Count + 50, 1000).ToString()) // Buffer for safety
            };

            return (targetResourceType, targetQuery);
        }

        /// <summary>
        /// Attempts to guess the target resource type from a reference field name.
        /// </summary>
        private string GuessTargetResourceType(string fieldName)
        {
            return fieldName.ToLowerInvariant() switch
            {
                "subject" => "Patient",
                "patient" => "Patient",
                "practitioner" => "Practitioner",
                "organization" => "Organization",
                "location" => "Location",
                "encounter" => "Encounter",
                "performer" => "Practitioner",
                "requester" => "Practitioner",
                _ => "Resource" // Generic fallback
            };
        }
    }
}
