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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Client;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Models;
using Polly;
using Polly.CircuitBreaker;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Configuration
{
    /// <summary>
    /// Orchestrates FHIR client operations across multiple server endpoints.
    /// </summary>
    public class FhirServerOrchestrator : IFhirServerOrchestrator
    {
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<FhirServerOrchestrator> _logger;
        private readonly Dictionary<string, IFhirClient> _fhirClients;
        private readonly Dictionary<string, IAsyncPolicy> _circuitBreakerPolicies;
        private readonly object _clientLock = new object();

        public FhirServerOrchestrator(
            IOptions<FanoutBrokerConfiguration> configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<FhirServerOrchestrator> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fhirClients = new Dictionary<string, IFhirClient>();
            _circuitBreakerPolicies = new Dictionary<string, IAsyncPolicy>();
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
                var circuitBreakerPolicy = GetOrCreateCircuitBreakerPolicy(server);

                // Execute search with circuit breaker protection
                var searchResult = await circuitBreakerPolicy.ExecuteAsync(async () =>
                {
                    var queryString = BuildQueryString(searchOptions);
                    var resourceType = searchOptions.GetType().GetProperty("ResourceType")?.GetValue(searchOptions) as string;
                    var requestUrl = string.IsNullOrEmpty(resourceType)
                        ? $"?{queryString}"
                        : $"{resourceType}?{queryString}";

                    _logger.LogDebug("Executing search on server {ServerId}: {RequestUrl}", 
                        server.Id, requestUrl);

                    var response = await fhirClient.SearchAsync(requestUrl, null, cancellationToken);

                    if (response.Resource is Bundle bundle)
                    {
                        return ConvertBundleToSearchResult(bundle, server);
                    }

                    throw new InvalidOperationException("Search response is not a Bundle");
                });

                result.SearchResult = searchResult;
                result.IsSuccess = true;
                result.StatusCode = 200;
            }
            catch (BrokenCircuitException ex)
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

        private IAsyncPolicy GetOrCreateCircuitBreakerPolicy(FhirServerEndpoint server)
        {
            if (!_configuration.Value.EnableCircuitBreaker)
            {
                return Policy.NoOpAsync();
            }

            lock (_clientLock)
            {
                if (_circuitBreakerPolicies.TryGetValue(server.Id, out var existingPolicy))
                {
                    return existingPolicy;
                }

                var policy = Policy
                    .Handle<Exception>()
                    .CircuitBreakerAsync(
                        exceptionsAllowedBeforeBreaking: _configuration.Value.CircuitBreakerFailureThreshold,
                        durationOfBreak: TimeSpan.FromSeconds(_configuration.Value.CircuitBreakerTimeoutSeconds),
                        onBreak: (ex, duration) =>
                        {
                            _logger.LogWarning("Circuit breaker opened for server {ServerId} for {Duration}s due to: {Exception}",
                                server.Id, duration.TotalSeconds, ex.Message);
                        },
                        onReset: () =>
                        {
                            _logger.LogInformation("Circuit breaker reset for server {ServerId}", server.Id);
                        },
                        onHalfOpen: () =>
                        {
                            _logger.LogInformation("Circuit breaker half-open for server {ServerId}", server.Id);
                        });

                _circuitBreakerPolicies[server.Id] = policy;
                return policy;
            }
        }

        private string BuildQueryString(SearchOptions searchOptions)
        {
            var queryParams = new List<string>();

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
                foreach (var param in searchOptions.UnsupportedSearchParams)
                {
                    queryParams.Add($"{Uri.EscapeDataString(param.Item1)}={Uri.EscapeDataString(param.Item2)}");
                }
            }

            return string.Join("&", queryParams);
        }

        private SearchResult ConvertBundleToSearchResult(Bundle bundle, FhirServerEndpoint server)
        {
            var results = new List<SearchResultEntry>();
            // TODO: Map Bundle entries to ResourceWrapper objects to populate SearchResultEntry list.

            // Extract continuation token from Bundle links
            var continuationToken = ExtractContinuationToken(bundle);

            return new SearchResult(
                results,
                continuationToken,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>(),
                searchIssues: null);
        }

        private string ExtractContinuationToken(Bundle bundle)
        {
            var nextLink = bundle.Link?.FirstOrDefault(l => l.Relation == "next");
            if (nextLink?.Url == null)
            {
                return null;
            }

            // Extract continuation token from next link URL
            var uri = new Uri(nextLink.Url);
            var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
            return queryParams["ct"];
        }
    }
}