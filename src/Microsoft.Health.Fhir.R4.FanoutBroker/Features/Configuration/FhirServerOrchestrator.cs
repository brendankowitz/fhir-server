// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core;
using Microsoft.Health.Fhir.Client;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.CircuitBreaker;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors;
using Microsoft.Health.Fhir.FanoutBroker.Models;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Configuration
{
    /// <summary>
    /// Orchestrates FHIR client operations across multiple server endpoints.
    /// </summary>
    public class FhirServerOrchestrator : IFhirServerOrchestrator
    {
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ICircuitBreakerFactory _circuitBreakerFactory;
        private readonly ILogger<FhirServerOrchestrator> _logger;
        private readonly IResourceDeserializer _resourceDeserializer;
        private readonly FhirJsonSerializer _jsonSerializer;
        private readonly Dictionary<string, IFhirClient> _fhirClients;
        private readonly object _clientLock = new object();

        public FhirServerOrchestrator(
            IOptions<FanoutBrokerConfiguration> configuration,
            IHttpClientFactory httpClientFactory,
            ICircuitBreakerFactory circuitBreakerFactory,
            ILogger<FhirServerOrchestrator> logger,
            IResourceDeserializer resourceDeserializer,
            FhirJsonSerializer jsonSerializer)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _circuitBreakerFactory = circuitBreakerFactory ?? throw new ArgumentNullException(nameof(circuitBreakerFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _resourceDeserializer = resourceDeserializer ?? throw new ArgumentNullException(nameof(resourceDeserializer));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _fhirClients = new Dictionary<string, IFhirClient>();
        }

        /// <inheritdoc />
        public IReadOnlyList<FhirServerEndpoint> GetEnabledServers()
        {
            return _configuration.Value.FhirServers?
                .Where(s => s.IsEnabled && !string.IsNullOrEmpty(s.BaseUrl))
                .ToList() ?? new List<FhirServerEndpoint>();
        }

        /// <inheritdoc />
        public async Task<ServerSearchResult> SearchAsync(
            FhirServerEndpoint server,
            SearchOptions searchOptions,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ServerSearchResult
            {
                ServerId = server.Id,
                ServerBaseUrl = server.BaseUrl
            };

            try
            {
                var fhirClient = GetOrCreateFhirClient(server);
                var circuitBreaker = _configuration.Value.EnableCircuitBreaker
                    ? _circuitBreakerFactory.GetCircuitBreaker(server.Id)
                    : null;

                // Apply timeout protection
                var timeoutSeconds = server.TimeoutSeconds ?? _configuration.Value.QueryTimeoutSeconds;
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                // Execute search with circuit breaker and timeout protection
                SearchResult searchResult = circuitBreaker != null
                    ? await circuitBreaker.ExecuteAsync(async (ct) =>
                        {
                            var queryString = BuildQueryString(searchOptions);
                            var resourceType = GetResourceTypeFromSearchOptions(searchOptions);
                            var requestUrl = string.IsNullOrEmpty(resourceType)
                                ? $"?{queryString}"
                                : $"{resourceType}?{queryString}";

                            var fullUrl = $"{server.BaseUrl.TrimEnd('/')}/{requestUrl.TrimStart('/')}";

                            _logger.LogInformation("📤 Fanout Query to {ServerId}: {FullUrl}", server.Id, fullUrl);
                            _logger.LogDebug("Query details - ResourceType: {ResourceType}, QueryString: {QueryString}, Timeout: {TimeoutSeconds}s",
                                resourceType ?? "system-level", queryString, timeoutSeconds);

                            var response = await fhirClient.SearchAsync(requestUrl, null, ct);

                            if (response.Resource is Bundle bundle)
                            {
                                return ConvertBundleToSearchResult(bundle, server);
                            }

                            throw new InvalidOperationException("Search response is not a Bundle");
                        }, combinedCts.Token)
                    : await ExecuteSearchDirectly(fhirClient, searchOptions, server, timeoutSeconds, combinedCts.Token);

                result.SearchResult = searchResult;
                result.IsSuccess = true;
                result.StatusCode = 200;
            }
            catch (CircuitBreakerOpenException ex)
            {
                _logger.LogWarning("Circuit breaker is open for server {ServerId}: {Message}",
                    server.Id, ex.Message);

                result.IsSuccess = false;
                result.ErrorMessage = "Server is currently unavailable (circuit breaker open)";
                result.StatusCode = 503;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error when searching server {ServerId}", server.Id);

                result.IsSuccess = false;
                result.ErrorMessage = $"HTTP error: {ex.Message}";
                result.StatusCode = 500;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("Timeout when searching server {ServerId}", server.Id);

                result.IsSuccess = false;
                result.ErrorMessage = "Request timeout";
                result.StatusCode = 408;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error when searching server {ServerId}", server.Id);

                result.IsSuccess = false;
                result.ErrorMessage = $"Unexpected error: {ex.Message}";
                result.StatusCode = 500;
            }
            finally
            {
                stopwatch.Stop();
                result.ResponseTimeMs = stopwatch.ElapsedMilliseconds;

                _logger.LogInformation("Search on server {ServerId} completed in {ElapsedMs}ms, Success: {IsSuccess}",
                    server.Id, result.ResponseTimeMs, result.IsSuccess);
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<ServerHealthResult> CheckHealthAsync(
            FhirServerEndpoint server,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ServerHealthResult
            {
                ServerId = server.Id
            };

            try
            {
                var fhirClient = GetOrCreateFhirClient(server);

                // Perform a simple metadata request to check health
                var response = await fhirClient.ReadAsync<CapabilityStatement>("metadata", cancellationToken);

                result.IsHealthy = response.Resource != null;
                result.StatusCode = 200;

                _logger.LogDebug("Health check for server {ServerId} successful", server.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for server {ServerId}", server.Id);

                result.IsHealthy = false;
                result.ErrorMessage = ex.Message;
                result.StatusCode = 500;
            }
            finally
            {
                stopwatch.Stop();
                result.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<ServerCapabilityResult> GetCapabilityStatementAsync(
            FhirServerEndpoint server,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ServerCapabilityResult
            {
                ServerId = server.Id
            };

            try
            {
                var fhirClient = GetOrCreateFhirClient(server);
                var response = await fhirClient.ReadAsync<CapabilityStatement>("metadata", cancellationToken);

                result.CapabilityStatement = response.Resource;
                result.IsSuccess = response.Resource != null;
                result.StatusCode = 200;

                _logger.LogDebug("Successfully retrieved capability statement from server {ServerId}", server.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve capability statement from server {ServerId}", server.Id);

                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
                result.StatusCode = 500;
            }
            finally
            {
                stopwatch.Stop();
                result.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }

        private IFhirClient GetOrCreateFhirClient(FhirServerEndpoint server)
        {
            lock (_clientLock)
            {
                if (_fhirClients.TryGetValue(server.Id, out var existingClient))
                {
                    return existingClient;
                }

                // CA2000: HttpClient from IHttpClientFactory is managed by the factory and should not be manually disposed.
#pragma warning disable CA2000 // Dispose objects before losing scope
                var httpClient = _httpClientFactory.CreateClient();
#pragma warning restore CA2000
                httpClient.BaseAddress = new Uri(server.BaseUrl);

                // Configure authentication
                ConfigureAuthentication(httpClient, server.Authentication);

                // Add custom headers
                if (server.Headers != null)
                {
                    foreach (var header in server.Headers)
                    {
                        httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
                    }
                }

                // Configure timeout
                var timeoutSeconds = server.TimeoutSeconds ?? _configuration.Value.SearchTimeoutSeconds;
                httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

                var fhirClient = new FhirClient(httpClient);
                _fhirClients[server.Id] = fhirClient;

                _logger.LogInformation("Created FHIR client for server {ServerId} at {BaseUrl}",
                    server.Id, server.BaseUrl);

                return fhirClient;
            }
        }

        private void ConfigureAuthentication(HttpClient httpClient, FhirServerAuthentication auth)
        {
            if (auth == null || auth.Type == AuthenticationType.None)
            {
                return;
            }

            switch (auth.Type)
            {
                case AuthenticationType.Bearer:
                    if (!string.IsNullOrEmpty(auth.BearerToken))
                    {
                        httpClient.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.BearerToken);
                    }
                    break;

                case AuthenticationType.Basic:
                    if (!string.IsNullOrEmpty(auth.Username) && !string.IsNullOrEmpty(auth.Password))
                    {
                        var credentials = Convert.ToBase64String(
                            System.Text.Encoding.ASCII.GetBytes($"{auth.Username}:{auth.Password}"));
                        httpClient.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                    }
                    break;

                case AuthenticationType.ClientCredentials:
                    // Note: In a real implementation, you would implement OAuth2 client credentials flow
                    // For now, we'll log a warning that it needs to be implemented
                    _logger.LogWarning("OAuth2 Client Credentials authentication is not yet implemented for server authentication");
                    break;
            }
        }

        private string BuildQueryString(SearchOptions searchOptions)
        {
            var queryParams = new List<string>();

            // Get the context resource type to avoid generating _type parameters for resource-specific endpoints
            var contextResourceType = GetResourceTypeFromSearchOptions(searchOptions);

            // Extract query parameters from search expressions using the visitor
            if (searchOptions.Expression != null)
            {
                var expressionExtractor = new ExpressionToQueryParameterExtractor(contextResourceType);
                searchOptions.Expression.AcceptVisitor(expressionExtractor, null);

                _logger.LogDebug("🔍 Expression extracted {Count} parameters: {Parameters}",
                    expressionExtractor.QueryParameters.Count,
                    string.Join(", ", expressionExtractor.QueryParameters.Select(p => $"{p.Item1}={p.Item2}")));

                // Add all extracted search parameters, but skip _type parameters when we have a context resource type
                foreach (var param in expressionExtractor.QueryParameters)
                {
                    // Skip _type parameters if we already have a context resource type (resource-specific endpoint)
                    if (param.Item1 == "_type" && !string.IsNullOrEmpty(contextResourceType))
                    {
                        _logger.LogDebug("🔍 Skipping _type parameter '{TypeParam}' because context resource type is '{ContextType}'",
                            param.Item2, contextResourceType);
                        continue;
                    }

                    queryParams.Add($"{Uri.EscapeDataString(param.Item1)}={Uri.EscapeDataString(param.Item2)}");
                }
            }

            // Add continuation token if present
            if (!string.IsNullOrEmpty(searchOptions.ContinuationToken))
            {
                queryParams.Add($"ct={Uri.EscapeDataString(searchOptions.ContinuationToken)}");
            }

            // Add count parameter
            if (searchOptions.MaxItemCount > 0)
            {
                queryParams.Add($"_count={searchOptions.MaxItemCount}");
            }

            // Add sort parameters
            if (searchOptions.Sort?.Any() == true)
            {
                var sortParams = searchOptions.Sort.Select(s =>
                    s.sortOrder == SortOrder.Ascending ? s.searchParameterInfo.Name : $"-{s.searchParameterInfo.Name}");
                queryParams.Add($"_sort={string.Join(",", sortParams)}");
            }

            // Add other unsupported search parameters (pass-through)
            if (searchOptions.UnsupportedSearchParams != null)
            {
                _logger.LogDebug("🔍 UnsupportedSearchParams {Count} parameters: {Parameters}",
                    searchOptions.UnsupportedSearchParams.Count,
                    string.Join(", ", searchOptions.UnsupportedSearchParams.Select(p => $"{p.Item1}={p.Item2}")));

                foreach (var param in searchOptions.UnsupportedSearchParams)
                {
                    // Skip internal resourceTypeHint parameter - it's only for fanout broker context
                    if (param.Item1 == "resourceTypeHint")
                    {
                        _logger.LogDebug("🔍 Skipping internal resourceTypeHint parameter '{ResourceTypeHint}'", param.Item2);
                        continue;
                    }

                    queryParams.Add($"{Uri.EscapeDataString(param.Item1)}={Uri.EscapeDataString(param.Item2)}");
                }
            }

            var finalQueryString = string.Join("&", queryParams);
            _logger.LogDebug("🔍 Final query string: {QueryString}", finalQueryString);

            return finalQueryString;
        }

        private SearchResult ConvertBundleToSearchResult(Bundle bundle, FhirServerEndpoint server)
        {
            if (bundle == null)
            {
                _logger.LogError("Bundle is null for server {ServerId}", server?.Id ?? "unknown");
                throw new ArgumentNullException(nameof(bundle));
            }

            // Performance optimization: Pre-allocate collections with expected capacity
            var entryCount = bundle.Entry?.Count ?? 0;
            var results = new List<SearchResultEntry>(entryCount);
            var searchIssues = new List<OperationOutcomeIssue>();

            try
            {
                // Process Bundle entries to create SearchResultEntry objects
                if (bundle.Entry?.Any() == true)
                {
                    // Performance optimization: Log processing details for large bundles
                    if (entryCount > 100)
                    {
                        _logger.LogInformation("Processing large bundle with {EntryCount} entries from server {ServerId}",
                            entryCount, server.Id);
                    }

                    foreach (var entry in bundle.Entry)
                    {
                        try
                        {
                            var searchResultEntry = ConvertBundleEntryToSearchResultEntry(entry, server);
                            if (searchResultEntry.HasValue)
                            {
                                results.Add(searchResultEntry.Value);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to convert bundle entry to search result entry for server {ServerId}", server.Id);

                            // Add issue but continue processing other entries
                            searchIssues.Add(new OperationOutcomeIssue(
                                OperationOutcomeConstants.IssueSeverity.Warning,
                                OperationOutcomeConstants.IssueType.Processing,
                                $"Failed to process entry: {ex.Message}"));
                        }
                    }

                    // Performance optimization: Trim excess capacity if we have fewer results than expected
                    if (results.Count < results.Capacity / 2)
                    {
                        results.TrimExcess();
                    }
                }

                // Extract continuation token from Bundle links
                var continuationToken = ExtractContinuationToken(bundle);

                // Extract sort order from Bundle if available
                var sortOrder = ExtractSortOrder(bundle);

                // Create the search result
                var searchResult = new SearchResult(
                    results,
                    continuationToken,
                    sortOrder: sortOrder,
                    unsupportedSearchParameters: new List<Tuple<string, string>>(),
                    searchIssues: searchIssues)
                {
                    SourceServer = server.BaseUrl,
                };

                // Set total count if available
                if (bundle.Total.HasValue)
                {
                    searchResult.TotalCount = (int)bundle.Total.Value;
                }

                // Set source server for traceability
                searchResult.SourceServer = server.BaseUrl;

                _logger.LogDebug("Converted bundle with {EntryCount} entries from server {ServerId}, continuation token: {HasToken}",
                    results.Count, server.Id, !string.IsNullOrEmpty(continuationToken));

                return searchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert bundle to search result for server {ServerId}", server.Id);
                throw new InvalidOperationException($"Failed to convert bundle from server {server.Id}: {ex.Message}", ex);
            }
        }

        private SearchResultEntry? ConvertBundleEntryToSearchResultEntry(Bundle.EntryComponent entry, FhirServerEndpoint server)
        {
            if (entry?.Resource == null)
            {
                _logger.LogDebug("Skipping bundle entry with no resource for server {ServerId}", server.Id);
                return null;
            }

            try
            {
                // Extract resource information
                var resource = entry.Resource;
                var resourceTypeName = resource.TypeName;
                var resourceId = resource.Id;
                var versionId = resource.VersionId;
                var lastModified = resource.Meta?.LastUpdated ?? DateTimeOffset.UtcNow;

                // Handle missing required fields
                if (string.IsNullOrEmpty(resourceId))
                {
                    _logger.LogWarning("Bundle entry has resource without ID for server {ServerId}, resource type: {ResourceType}",
                        server.Id, resourceTypeName);
                    return null;
                }

                // Validate resource type name
                if (string.IsNullOrEmpty(resourceTypeName))
                {
                    _logger.LogWarning("Bundle entry has resource without type name for server {ServerId}, resource ID: {ResourceId}",
                        server.Id, resourceId);
                    return null;
                }

                // Serialize the resource to create RawResource
                string resourceJson;
                try
                {
                    resourceJson = _jsonSerializer.SerializeToString(resource);
                }
                catch (Exception serializationEx)
                {
                    _logger.LogError(serializationEx, "Failed to serialize resource {ResourceType}/{ResourceId} from server {ServerId}",
                        resourceTypeName, resourceId, server.Id);
                    throw new InvalidOperationException($"Failed to serialize resource {resourceTypeName}/{resourceId}", serializationEx);
                }

                // Validate resource size against configured limits
                var resourceSizeBytes = System.Text.Encoding.UTF8.GetByteCount(resourceJson);
                var resourceSizeKB = resourceSizeBytes / 1024;
                if (resourceSizeKB > _configuration.Value.MaxResourceSizeKB)
                {
                    _logger.LogWarning("Resource {ResourceType}/{ResourceId} from server {ServerId} exceeds size limit: {SizeKB}KB > {MaxKB}KB",
                        resourceTypeName, resourceId, server.Id, resourceSizeKB, _configuration.Value.MaxResourceSizeKB);
                    return null; // Skip oversized resources
                }

                var rawResource = new RawResource(resourceJson, FhirResourceFormat.Json, isMetaSet: true);

                // Create a minimal ResourceRequest for the wrapper
                var resourceRequest = new ResourceRequest("GET", new Uri($"{server.BaseUrl}/{resourceTypeName}/{resourceId}"));

                // Create ResourceWrapper with minimal required data
                var resourceWrapper = new ResourceWrapper(
                    resourceId: resourceId,
                    versionId: versionId,
                    resourceTypeName: resourceTypeName,
                    rawResource: rawResource,
                    request: resourceRequest,
                    lastModified: lastModified,
                    deleted: false,
                    searchIndices: new List<SearchIndexEntry>(),
                    compartmentIndices: null,
                    lastModifiedClaims: new List<KeyValuePair<string, string>>());

                // Extract search entry mode from Bundle entry
                var searchEntryMode = SearchEntryMode.Match; // Default
                if (entry.Search?.Mode == Bundle.SearchEntryMode.Include)
                {
                    searchEntryMode = SearchEntryMode.Include;
                }
                else if (entry.Search?.Mode == Bundle.SearchEntryMode.Outcome)
                {
                    searchEntryMode = SearchEntryMode.Outcome;
                }

                // Add server context to fullUrl for traceability
                if (!string.IsNullOrEmpty(entry.FullUrl) && !entry.FullUrl.StartsWith(server.BaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Entry fullUrl '{FullUrl}' does not match server base URL '{BaseUrl}' for resource {ResourceType}/{ResourceId}",
                        entry.FullUrl, server.BaseUrl, resourceTypeName, resourceId);
                }

                return new SearchResultEntry(resourceWrapper, searchEntryMode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create ResourceWrapper for resource {ResourceType}/{ResourceId} from server {ServerId}",
                    entry.Resource?.TypeName, entry.Resource?.Id, server.Id);
                throw;
            }
        }

        private string ExtractContinuationToken(Bundle bundle)
        {
            var nextLink = bundle.Link?.FirstOrDefault(l => l.Relation == "next");
            if (nextLink?.Url == null)
            {
                return null;
            }

            try
            {
                // Extract continuation token from next link URL
                var uri = new Uri(nextLink.Url);
                var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);

                // Try different common continuation token parameter names
                return queryParams["ct"] ?? queryParams["_continuation"] ?? queryParams["__continuation"];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse continuation token from next link: {NextUrl}", nextLink.Url);
                return null;
            }
        }

        /// <summary>
        /// Extracts sort order information from Bundle links if available.
        /// </summary>
        private IReadOnlyList<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)> ExtractSortOrder(Bundle bundle)
        {
            // Look for sort information in the Bundle's self link
            var selfLink = bundle.Link?.FirstOrDefault(l => l.Relation == "self");
            if (selfLink?.Url == null)
            {
                return null;
            }

            try
            {
                var uri = new Uri(selfLink.Url);
                var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var sortParam = queryParams["_sort"];

                if (string.IsNullOrEmpty(sortParam))
                {
                    return null;
                }

                // Parse sort parameter (e.g., "_sort=date,-name")
                var sortItems = new List<(SearchParameterInfo, SortOrder)>();
                var sortValues = sortParam.Split(',', StringSplitOptions.RemoveEmptyEntries);

                foreach (var sortValue in sortValues)
                {
                    var trimmedValue = sortValue.Trim();
                    var sortOrder = SortOrder.Ascending;
                    var paramName = trimmedValue;

                    if (trimmedValue.StartsWith('-'))
                    {
                        sortOrder = SortOrder.Descending;
                        paramName = trimmedValue.Substring(1);
                    }

                    // Create a minimal SearchParameterInfo (in a real implementation, you'd look this up from a registry)
                    var searchParamInfo = new SearchParameterInfo(paramName, paramName, Microsoft.Health.Fhir.ValueSets.SearchParamType.String, null, null, null);
                    sortItems.Add((searchParamInfo, sortOrder));
                }

                return sortItems.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse sort order from self link: {SelfUrl}", selfLink.Url);
                return null;
            }
        }

        /// <summary>
        /// Extracts the resource type from SearchOptions without using reflection.
        /// This replaces the reflection-based approach with proper API usage.
        /// </summary>
        private string GetResourceTypeFromSearchOptions(SearchOptions searchOptions)
        {
            var unsupportedParamsStr = searchOptions.UnsupportedSearchParams != null
                ? string.Join(", ", searchOptions.UnsupportedSearchParams.Select(p => $"{p.Item1}={p.Item2}"))
                : "null";
            _logger.LogDebug("🔍 GetResourceTypeFromSearchOptions - UnsupportedSearchParams: {UnsupportedParams}", unsupportedParamsStr);

            // Try to get resource type from UnsupportedSearchParams hints
            var resourceTypeHint = searchOptions.UnsupportedSearchParams?.FirstOrDefault(p => p.Item1 == "resourceTypeHint")?.Item2;
            if (!string.IsNullOrWhiteSpace(resourceTypeHint))
            {
                _logger.LogDebug("🔍 Found resourceTypeHint: {ResourceTypeHint}", resourceTypeHint);
                return resourceTypeHint;
            }

            // Check if there's a _type parameter that specifies the resource type
            var typeParam = searchOptions.UnsupportedSearchParams?.FirstOrDefault(p => p.Item1 == "_type")?.Item2;
            if (!string.IsNullOrWhiteSpace(typeParam))
            {
                // If multiple types are specified, take the first one
                var resourceType = typeParam.Split(',')[0].Trim();
                _logger.LogDebug("🔍 Found _type parameter, using first: {ResourceType}", resourceType);
                return resourceType;
            }

            // No resource type found - this is likely a system-level search
            _logger.LogDebug("🔍 No resource type found - system-level search");
            return null;
        }

        /// <summary>
        /// Executes search directly without circuit breaker protection.
        /// </summary>
        private async Task<SearchResult> ExecuteSearchDirectly(
            IFhirClient fhirClient,
            SearchOptions searchOptions,
            FhirServerEndpoint server,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var queryString = BuildQueryString(searchOptions);
            var resourceType = GetResourceTypeFromSearchOptions(searchOptions);
            var requestUrl = string.IsNullOrEmpty(resourceType)
                ? $"?{queryString}"
                : $"{resourceType}?{queryString}";

            var fullUrl = $"{server.BaseUrl.TrimEnd('/')}/{requestUrl.TrimStart('/')}";

            _logger.LogInformation("📤 Fanout Query to {ServerId}: {FullUrl}", server.Id, fullUrl);
            _logger.LogDebug("Query details - ResourceType: {ResourceType}, QueryString: {QueryString}, Timeout: {TimeoutSeconds}s",
                resourceType ?? "system-level", queryString, timeoutSeconds);

            var response = await fhirClient.SearchAsync(requestUrl, null, cancellationToken);

            if (response.Resource is Bundle bundle)
            {
                return ConvertBundleToSearchResult(bundle, server);
            }

            throw new InvalidOperationException("Search response is not a Bundle");
        }
    }
}
