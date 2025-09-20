// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Protection
{
    /// <summary>
    /// Implementation of resource protection service that enforces configured limits.
    /// </summary>
    public class ResourceProtectionService : IResourceProtectionService
    {
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<ResourceProtectionService> _logger;

        // Concurrent tracking of active operations
        private readonly ConcurrentDictionary<string, SearchOperationToken> _activeOperations = new();

        // Rate limiting per client
        private readonly ConcurrentDictionary<string, ClientRateLimit> _clientRateLimits = new();

        // Process for memory monitoring
        private readonly Process _currentProcess = Process.GetCurrentProcess();

        public ResourceProtectionService(
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<ResourceProtectionService> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<ResourceProtectionResult> ValidateSearchRequestAsync(
            SearchOptions searchOptions,
            string clientId = null,
            CancellationToken cancellationToken = default)
        {
            var config = _configuration.Value;

            if (!config.EnableResourceProtection)
            {
                return Task.FromResult(new ResourceProtectionResult { IsAllowed = true });
            }

            // Check memory usage
            if (!IsMemoryUsageAcceptable())
            {
                _logger.LogWarning("Search request rejected due to high memory usage");
                return Task.FromResult(new ResourceProtectionResult
                {
                    IsAllowed = false,
                    RejectionReason = "Server is currently under high memory pressure. Please try again later.",
                    SuggestedStatusCode = 503 // Service Unavailable
                });
            }

            // Check concurrent operations
            if (_activeOperations.Count >= config.MaxConcurrentSearches)
            {
                _logger.LogWarning("Search request rejected due to maximum concurrent operations limit: {CurrentOps}/{MaxOps}",
                    _activeOperations.Count, config.MaxConcurrentSearches);
                return Task.FromResult(new ResourceProtectionResult
                {
                    IsAllowed = false,
                    RejectionReason = "Server is currently handling the maximum number of concurrent searches. Please try again later.",
                    SuggestedStatusCode = 503 // Service Unavailable
                });
            }

            // Check rate limiting per client
            if (!string.IsNullOrEmpty(clientId))
            {
                var rateLimit = _clientRateLimits.GetOrAdd(clientId, _ => new ClientRateLimit());
                if (!rateLimit.TryConsumeRequest(config.MaxQueriesPerMinute))
                {
                    _logger.LogWarning("Search request rejected for client {ClientId} due to rate limiting", clientId);
                    return Task.FromResult(new ResourceProtectionResult
                    {
                        IsAllowed = false,
                        RejectionReason = "Rate limit exceeded. Please reduce the frequency of requests.",
                        SuggestedStatusCode = 429 // Too Many Requests
                    });
                }
            }

            // Check if result count limit would be exceeded
            var requestedCount = searchOptions.MaxItemCount > 0 ? searchOptions.MaxItemCount : 50; // Default FHIR page size
            if (requestedCount > config.MaxTotalResults)
            {
                _logger.LogWarning("Search request rejected due to excessive result count: {RequestedCount}/{MaxResults}",
                    requestedCount, config.MaxTotalResults);
                return Task.FromResult(new ResourceProtectionResult
                {
                    IsAllowed = false,
                    RejectionReason = $"Requested result count ({requestedCount}) exceeds maximum allowed ({config.MaxTotalResults}).",
                    SuggestedStatusCode = 400 // Bad Request
                });
            }

            return Task.FromResult(new ResourceProtectionResult { IsAllowed = true });
        }

        public bool IsMemoryUsageAcceptable()
        {
            try
            {
                var config = _configuration.Value;
                if (!config.EnableResourceProtection)
                {
                    return true;
                }

                // Get current memory usage in MB
                _currentProcess.Refresh();
                var memoryUsageMB = _currentProcess.WorkingSet64 / (1024 * 1024);

                var isAcceptable = memoryUsageMB <= config.MaxMemoryUsageMB;

                if (!isAcceptable)
                {
                    _logger.LogWarning("Memory usage threshold exceeded: {CurrentMB}MB / {MaxMB}MB",
                        memoryUsageMB, config.MaxMemoryUsageMB);
                }

                return isAcceptable;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking memory usage, allowing request");
                return true; // Fail open for availability
            }
        }

        public bool ValidateResultCount(int currentResultCount, int estimatedAdditionalResults = 0)
        {
            var config = _configuration.Value;
            if (!config.EnableResourceProtection)
            {
                return true;
            }

            var totalCount = currentResultCount + estimatedAdditionalResults;
            var isValid = totalCount <= config.MaxTotalResults;

            if (!isValid)
            {
                _logger.LogWarning("Result count limit would be exceeded: {TotalCount}/{MaxResults}",
                    totalCount, config.MaxTotalResults);
            }

            return isValid;
        }

        public bool ValidateResourceSize(long resourceSizeBytes)
        {
            var config = _configuration.Value;
            if (!config.EnableResourceProtection)
            {
                return true;
            }

            var resourceSizeKB = resourceSizeBytes / 1024;
            var isValid = resourceSizeKB <= config.MaxResourceSizeKB;

            if (!isValid)
            {
                _logger.LogWarning("Resource size limit exceeded: {SizeKB}KB / {MaxKB}KB",
                    resourceSizeKB, config.MaxResourceSizeKB);
            }

            return isValid;
        }

        public async Task<SearchOperationToken> BeginSearchOperationAsync()
        {
            var config = _configuration.Value;
            if (!config.EnableResourceProtection)
            {
                return new SearchOperationToken(); // Return a token but don't track it
            }

            // Check if we can accept another operation
            if (_activeOperations.Count >= config.MaxConcurrentSearches)
            {
                _logger.LogWarning("Cannot begin search operation - maximum concurrent operations reached: {Count}/{Max}",
                    _activeOperations.Count, config.MaxConcurrentSearches);
                return null;
            }

            var token = new SearchOperationToken();
            if (_activeOperations.TryAdd(token.OperationId, token))
            {
                _logger.LogDebug("Search operation started: {OperationId}, Active operations: {Count}",
                    token.OperationId, _activeOperations.Count);

                // Clean up old operations that might have been abandoned
                await CleanupAbandonedOperationsAsync();

                return token;
            }

            return null;
        }

        public void EndSearchOperation(SearchOperationToken token)
        {
            if (token?.OperationId == null)
            {
                return;
            }

            if (_activeOperations.TryRemove(token.OperationId, out var removedToken))
            {
                var duration = DateTimeOffset.UtcNow - removedToken.StartTime;
                _logger.LogDebug("Search operation completed: {OperationId}, Duration: {Duration}ms, Active operations: {Count}",
                    token.OperationId, duration.TotalMilliseconds, _activeOperations.Count);
            }
        }

        private async Task CleanupAbandonedOperationsAsync()
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10); // Operations older than 10 minutes are considered abandoned

            var abandonedOperations = _activeOperations
                .Where(kvp => kvp.Value.StartTime < cutoff)
                .ToList();

            foreach (var (operationId, _) in abandonedOperations)
            {
                if (_activeOperations.TryRemove(operationId, out _))
                {
                    _logger.LogWarning("Cleaned up abandoned search operation: {OperationId}", operationId);
                }
            }

            await Task.Delay(1); // Prevent compiler warning about async method without await
        }

        private class ClientRateLimit
        {
            private readonly object _lock = new object();
            private DateTime _windowStart = DateTime.UtcNow;
            private int _requestCount = 0;

            public bool TryConsumeRequest(int maxRequestsPerMinute)
            {
                lock (_lock)
                {
                    var now = DateTime.UtcNow;

                    // Reset window if it's been more than a minute
                    if (now - _windowStart >= TimeSpan.FromMinutes(1))
                    {
                        _windowStart = now;
                        _requestCount = 0;
                    }

                    if (_requestCount >= maxRequestsPerMinute)
                    {
                        return false;
                    }

                    _requestCount++;
                    return true;
                }
            }
        }
    }
}