// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Features.Conformance;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.QueryOptimization
{
    /// <summary>
    /// Advanced query optimization service with cost analysis and server capability awareness.
    /// </summary>
    public class QueryOptimizationService : IQueryOptimizationService, IDisposable
    {
        private readonly IFanoutCapabilityStatementProvider _capabilityProvider;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<QueryOptimizationService> _logger;

        // Performance metrics storage (in-memory for now, could be persisted)
        private readonly ConcurrentDictionary<string, ServerPerformanceMetrics> _serverMetrics;
        private readonly ConcurrentDictionary<string, List<QueryMetric>> _queryHistory;
        private readonly Timer _metricsCleanupTimer;

        public QueryOptimizationService(
            IFanoutCapabilityStatementProvider capabilityProvider,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<QueryOptimizationService> logger)
        {
            _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _serverMetrics = new ConcurrentDictionary<string, ServerPerformanceMetrics>();
            _queryHistory = new ConcurrentDictionary<string, List<QueryMetric>>();

            // Clean up old metrics every hour
            _metricsCleanupTimer = new Timer(CleanupOldMetrics, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        public async Task<QueryCostAnalysis> AnalyzeQueryCostAsync(SearchOptions searchOptions, string resourceType)
        {
            var analysis = new QueryCostAnalysis
            {
                EstimatedCost = CalculateBaseCost(),
                CostFactors = new List<string>(),
                OptimizationRecommendations = new List<string>()
            };

            // Analyze search parameters complexity
            if (searchOptions.Expression != null)
            {
                var parameterCount = CountSearchParameters(searchOptions);
                analysis.EstimatedCost += parameterCount * 5;
                analysis.CostFactors.Add($"Search parameters: {parameterCount}");

                if (parameterCount > 10)
                {
                    analysis.OptimizationRecommendations.Add("Consider reducing the number of search parameters");
                }
            }

            // Analyze sorting requirements
            if (searchOptions.Sort?.Any() == true)
            {
                var sortComplexity = AnalyzeSortComplexity(searchOptions.Sort);
                analysis.EstimatedCost += sortComplexity;
                analysis.CostFactors.Add($"Sort complexity: {sortComplexity}");

                if (sortComplexity > 20)
                {
                    analysis.OptimizationRecommendations.Add("Consider simplifying sort criteria or removing sorting");
                }
            }

            // Analyze result count
            var maxResults = searchOptions.MaxItemCount > 0 ? searchOptions.MaxItemCount : 50;
            if (maxResults > 100)
            {
                analysis.EstimatedCost += (maxResults - 100) / 10;
                analysis.CostFactors.Add($"Large result set: {maxResults}");
                analysis.OptimizationRecommendations.Add("Consider reducing the page size for better performance");
            }

            // Check for expensive operations like _include
            if (HasIncludeOperations(searchOptions))
            {
                analysis.EstimatedCost += 25;
                analysis.CostFactors.Add("Include operations detected");
                analysis.OptimizationRecommendations.Add("Include operations can be expensive across multiple servers");
            }

            // Determine if this is an expensive query
            analysis.IsExpensiveQuery = analysis.EstimatedCost > 50;

            // Estimate execution time and result count based on historical data
            var historicalMetrics = await GetHistoricalMetricsAsync(resourceType, searchOptions);
            if (historicalMetrics.Any())
            {
                analysis.EstimatedExecutionTimeMs = (int)historicalMetrics.Average(m => m.ExecutionTimeMs);
                analysis.EstimatedResultCount = (int)historicalMetrics.Average(m => m.ResultCount);
            }
            else
            {
                // Default estimates
                analysis.EstimatedExecutionTimeMs = analysis.EstimatedCost * 100;
                analysis.EstimatedResultCount = Math.Min(maxResults, 50);
            }

            _logger.LogDebug("Query cost analysis for {ResourceType}: Cost={Cost}, Time={Time}ms, Results={Results}",
                resourceType, analysis.EstimatedCost, analysis.EstimatedExecutionTimeMs, analysis.EstimatedResultCount);

            return analysis;
        }

        public async Task<QueryExecutionPlan> OptimizeServerSelectionAsync(
            IReadOnlyList<FhirServerEndpoint> availableServers,
            SearchOptions searchOptions,
            string resourceType,
            CancellationToken cancellationToken = default)
        {
            var plan = new QueryExecutionPlan
            {
                OptimizationReasoning = new List<string>()
            };

            // Get server capabilities
            var serverCapabilities = await _capabilityProvider.GetServerCapabilitiesAsync(cancellationToken);

            // Filter servers based on capability support
            var capableServers = new List<FhirServerEndpoint>();
            foreach (var server in availableServers)
            {
                if (serverCapabilities.TryGetValue(server.Id, out var capability) &&
                    capability.IsSuccess &&
                    SupportsQuery(capability.CapabilityStatement, resourceType, searchOptions))
                {
                    capableServers.Add(server);
                }
                else
                {
                    plan.ExcludedServers.Add(new ExcludedServer
                    {
                        Server = server,
                        ExclusionReason = "Server does not support required search parameters"
                    });
                }
            }

            if (!capableServers.Any())
            {
                _logger.LogWarning("No servers support the requested query for {ResourceType}", resourceType);
                plan.SelectedServers = availableServers.ToList(); // Fallback to all servers
                plan.OptimizationReasoning.Add("No optimal servers found, using all available servers");
                return plan;
            }

            // Score servers based on performance metrics
            var scoredServers = new List<(FhirServerEndpoint server, double score)>();
            foreach (var server in capableServers)
            {
                var performance = await GetServerPerformanceAsync(server.Id);
                var score = CalculateServerScore(server, performance, resourceType, searchOptions);
                scoredServers.Add((server, score));
            }

            // Sort by score (highest first)
            scoredServers.Sort((a, b) => b.score.CompareTo(a.score));

            // Recommend execution strategy
            var strategy = await RecommendExecutionStrategyAsync(searchOptions, resourceType, capableServers);
            plan.ExecutionStrategy = strategy;

            // Select servers based on strategy
            switch (strategy)
            {
                case QueryExecutionStrategy.SingleBest:
                    plan.SelectedServers.Add(scoredServers.First().server);
                    plan.OptimizationReasoning.Add($"Selected single best server based on performance metrics");
                    break;

                case QueryExecutionStrategy.ParallelOptimal:
                    var topServers = scoredServers.Take(Math.Min(3, scoredServers.Count)).Select(s => s.server).ToList();
                    plan.SelectedServers.AddRange(topServers);
                    plan.OptimizationReasoning.Add($"Selected top {topServers.Count} servers for parallel execution");
                    break;

                case QueryExecutionStrategy.ParallelAll:
                default:
                    plan.SelectedServers.AddRange(scoredServers.Select(s => s.server));
                    plan.OptimizationReasoning.Add("Using all capable servers for maximum coverage");
                    break;
            }

            // Calculate confidence level
            plan.ConfidenceLevel = CalculateConfidenceLevel(scoredServers, plan.SelectedServers.Count);

            // Estimate execution time
            plan.EstimatedTotalTimeMs = EstimateExecutionTime(plan.SelectedServers, plan.ExecutionStrategy);

            _logger.LogInformation("Query optimization plan for {ResourceType}: Strategy={Strategy}, Servers={ServerCount}, Confidence={Confidence}%",
                resourceType, strategy, plan.SelectedServers.Count, plan.ConfidenceLevel);

            return plan;
        }

        public async Task RecordQueryMetricsAsync(
            string serverId,
            SearchOptions searchOptions,
            string resourceType,
            long executionTimeMs,
            int resultCount,
            bool wasSuccessful)
        {
            var metric = new QueryMetric
            {
                ServerId = serverId,
                ResourceType = resourceType,
                ExecutionTimeMs = executionTimeMs,
                ResultCount = resultCount,
                WasSuccessful = wasSuccessful,
                Timestamp = DateTimeOffset.UtcNow,
                QueryHash = CalculateQueryHash(searchOptions, resourceType)
            };

            // Add to query history
            var historyKey = $"{resourceType}:{metric.QueryHash}";
            _queryHistory.AddOrUpdate(historyKey,
                new List<QueryMetric> { metric },
                (key, existing) =>
                {
                    existing.Add(metric);
                    // Keep only recent history (last 100 queries)
                    return existing.OrderByDescending(m => m.Timestamp).Take(100).ToList();
                });

            // Update server performance metrics
            _serverMetrics.AddOrUpdate(serverId,
                CreateInitialServerMetrics(serverId, metric),
                (key, existing) => UpdateServerMetrics(existing, metric));

            await Task.CompletedTask; // For potential async persistence in the future
        }

        public Task<ServerPerformanceMetrics> GetServerPerformanceAsync(string serverId)
        {
            if (_serverMetrics.TryGetValue(serverId, out var metrics))
            {
                return Task.FromResult(metrics);
            }

            // Return default metrics for unknown servers
            return Task.FromResult(new ServerPerformanceMetrics
            {
                ServerId = serverId,
                AverageResponseTimeMs = 1000, // Default assumption
                SuccessRate = 0.95, // Assume good success rate initially
                TotalQueries = 0,
                IsHealthy = true,
                PerformanceRating = 5, // Neutral rating
                LastUpdated = DateTimeOffset.UtcNow
            });
        }

        public async Task<QueryExecutionStrategy> RecommendExecutionStrategyAsync(
            SearchOptions searchOptions,
            string resourceType,
            IReadOnlyList<FhirServerEndpoint> availableServers)
        {
            var costAnalysis = await AnalyzeQueryCostAsync(searchOptions, resourceType);

            // For expensive queries, be more selective
            if (costAnalysis.IsExpensiveQuery)
            {
                if (availableServers.Count > 3)
                {
                    return QueryExecutionStrategy.ParallelOptimal;
                }
                return QueryExecutionStrategy.SingleBest;
            }

            // For simple queries with sorting, parallel execution works well
            if (searchOptions.Sort?.Any() == true)
            {
                return QueryExecutionStrategy.ParallelAll;
            }

            // For queries with many servers, use optimal subset
            if (availableServers.Count > 5)
            {
                return QueryExecutionStrategy.ParallelOptimal;
            }

            // Default to parallel execution across all servers
            return QueryExecutionStrategy.ParallelAll;
        }

        private int CalculateBaseCost()
        {
            return 10; // Base cost for any query
        }

        private int CountSearchParameters(SearchOptions searchOptions)
        {
            // This is a simplified count - in a real implementation,
            // you would parse the expression tree to count parameters
            if (searchOptions.UnsupportedSearchParams?.Any() == true)
            {
                return searchOptions.UnsupportedSearchParams.Count;
            }
            return 1; // At least one parameter assumed
        }

        private int AnalyzeSortComplexity(IReadOnlyList<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)> sortParams)
        {
            var complexity = sortParams.Count * 10;

            // Add complexity for multiple sort parameters
            if (sortParams.Count > 2)
            {
                complexity += (sortParams.Count - 2) * 5;
            }

            return complexity;
        }

        private bool HasIncludeOperations(SearchOptions searchOptions)
        {
            return searchOptions.UnsupportedSearchParams?.Any(p =>
                p.Item1.Equals("_include", StringComparison.OrdinalIgnoreCase) ||
                p.Item1.Equals("_revinclude", StringComparison.OrdinalIgnoreCase)) == true;
        }

        private Task<List<QueryMetric>> GetHistoricalMetricsAsync(string resourceType, SearchOptions searchOptions)
        {
            var queryHash = CalculateQueryHash(searchOptions, resourceType);
            var historyKey = $"{resourceType}:{queryHash}";

            if (_queryHistory.TryGetValue(historyKey, out var history))
            {
                // Return recent successful queries
                return Task.FromResult(history.Where(m => m.WasSuccessful && m.Timestamp > DateTimeOffset.UtcNow.AddDays(-7))
                             .ToList());
            }

            return Task.FromResult(new List<QueryMetric>());
        }

        private bool SupportsQuery(Hl7.Fhir.Model.CapabilityStatement capabilityStatement, string resourceType, SearchOptions searchOptions)
        {
            if (capabilityStatement?.Rest == null)
                return false;

            var restComponent = capabilityStatement.Rest.FirstOrDefault();
            if (restComponent?.Resource == null)
                return false;

            // Check if resource type is supported
            var resourceCapability = restComponent.Resource.FirstOrDefault(r =>
                r.Type?.ToString().Equals(resourceType, StringComparison.OrdinalIgnoreCase) == true);

            if (resourceCapability == null)
                return false;

            // For now, assume the server supports the query if it supports the resource type
            // In a more sophisticated implementation, you would check individual search parameters
            return true;
        }

        private double CalculateServerScore(FhirServerEndpoint server, ServerPerformanceMetrics performance, string resourceType, SearchOptions searchOptions)
        {
            double score = 0;

            // Base score from performance rating
            score += performance.PerformanceRating * 10;

            // Factor in success rate
            score += performance.SuccessRate * 20;

            // Factor in response time (lower is better)
            if (performance.AverageResponseTimeMs > 0)
            {
                score += Math.Max(0, 50 - (performance.AverageResponseTimeMs / 100));
            }

            // Bonus for servers that handle this resource type well
            if (performance.OptimalResourceTypes.Contains(resourceType))
            {
                score += 15;
            }

            // Health check
            if (!performance.IsHealthy)
            {
                score *= 0.5; // Significant penalty for unhealthy servers
            }

            return Math.Max(0, score);
        }

        private int CalculateConfidenceLevel(List<(FhirServerEndpoint server, double score)> scoredServers, int selectedCount)
        {
            if (!scoredServers.Any())
                return 0;

            // Higher confidence when there's clear differentiation in scores
            var topScore = scoredServers.First().score;
            var averageScore = scoredServers.Average(s => s.score);
            var scoreDifferential = (topScore - averageScore) / Math.Max(averageScore, 1);

            var baseConfidence = Math.Min(90, 50 + (int)(scoreDifferential * 100));

            // Reduce confidence if we're using many servers (less selective)
            if (selectedCount > scoredServers.Count * 0.8)
            {
                baseConfidence -= 20;
            }

            return Math.Max(10, baseConfidence);
        }

        private int EstimateExecutionTime(List<FhirServerEndpoint> servers, QueryExecutionStrategy strategy)
        {
            if (!servers.Any())
                return 0;

            var avgResponseTimes = new List<double>();
            foreach (var server in servers)
            {
                if (_serverMetrics.TryGetValue(server.Id, out var metrics))
                {
                    avgResponseTimes.Add(metrics.AverageResponseTimeMs);
                }
                else
                {
                    avgResponseTimes.Add(1000); // Default estimate
                }
            }

            switch (strategy)
            {
                case QueryExecutionStrategy.SingleBest:
                    return (int)avgResponseTimes.Min();

                case QueryExecutionStrategy.SequentialFallback:
                    return (int)avgResponseTimes.Sum();

                case QueryExecutionStrategy.ParallelAll:
                case QueryExecutionStrategy.ParallelOptimal:
                case QueryExecutionStrategy.RoutedByQuery:
                default:
                    // Parallel execution - time is dominated by slowest server
                    return (int)(avgResponseTimes.Max() * 1.2); // Add 20% overhead for coordination
            }
        }

        private string CalculateQueryHash(SearchOptions searchOptions, string resourceType)
        {
            var hashComponents = new List<string> { resourceType };

            if (searchOptions.UnsupportedSearchParams?.Any() == true)
            {
                hashComponents.AddRange(searchOptions.UnsupportedSearchParams.Select(p => $"{p.Item1}={p.Item2}"));
            }

            if (searchOptions.Sort?.Any() == true)
            {
                hashComponents.AddRange(searchOptions.Sort.Select(s => $"sort:{s.searchParameterInfo.Name}:{s.sortOrder}"));
            }

            hashComponents.Add($"count:{searchOptions.MaxItemCount}");

            return string.Join("|", hashComponents.OrderBy(h => h)).GetHashCode(StringComparison.Ordinal).ToString();
        }

        private ServerPerformanceMetrics CreateInitialServerMetrics(string serverId, QueryMetric metric)
        {
            return new ServerPerformanceMetrics
            {
                ServerId = serverId,
                AverageResponseTimeMs = metric.ExecutionTimeMs,
                SuccessRate = metric.WasSuccessful ? 1.0 : 0.0,
                TotalQueries = 1,
                AverageResultsPerQuery = metric.ResultCount,
                IsHealthy = metric.WasSuccessful,
                PerformanceRating = CalculateInitialRating(metric),
                LastUpdated = DateTimeOffset.UtcNow
            };
        }

        private ServerPerformanceMetrics UpdateServerMetrics(ServerPerformanceMetrics existing, QueryMetric newMetric)
        {
            var totalQueries = existing.TotalQueries + 1;
            var successCount = (existing.SuccessRate * existing.TotalQueries) + (newMetric.WasSuccessful ? 1 : 0);

            existing.AverageResponseTimeMs = ((existing.AverageResponseTimeMs * existing.TotalQueries) + newMetric.ExecutionTimeMs) / totalQueries;
            existing.SuccessRate = successCount / totalQueries;
            existing.TotalQueries = totalQueries;
            existing.AverageResultsPerQuery = ((existing.AverageResultsPerQuery * (existing.TotalQueries - 1)) + newMetric.ResultCount) / totalQueries;
            existing.IsHealthy = existing.SuccessRate > 0.8 && existing.AverageResponseTimeMs < 5000;
            existing.PerformanceRating = CalculatePerformanceRating(existing);
            existing.LastUpdated = DateTimeOffset.UtcNow;

            return existing;
        }

        private int CalculateInitialRating(QueryMetric metric)
        {
            var rating = 5; // Start neutral

            if (metric.WasSuccessful)
                rating += 2;

            if (metric.ExecutionTimeMs < 1000)
                rating += 2;
            else if (metric.ExecutionTimeMs > 5000)
                rating -= 2;

            return Math.Max(1, Math.Min(10, rating));
        }

        private int CalculatePerformanceRating(ServerPerformanceMetrics metrics)
        {
            var rating = 1;

            // Success rate contribution (0-4 points)
            rating += (int)(metrics.SuccessRate * 4);

            // Response time contribution (0-4 points)
            if (metrics.AverageResponseTimeMs < 500)
                rating += 4;
            else if (metrics.AverageResponseTimeMs < 1000)
                rating += 3;
            else if (metrics.AverageResponseTimeMs < 2000)
                rating += 2;
            else if (metrics.AverageResponseTimeMs < 5000)
                rating += 1;

            // Health contribution (0-2 points)
            if (metrics.IsHealthy)
                rating += 2;

            return Math.Max(1, Math.Min(10, rating));
        }

        private void CleanupOldMetrics(object state)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
                var keysToRemove = new List<string>();

                foreach (var kvp in _queryHistory)
                {
                    var filteredHistory = kvp.Value.Where(m => m.Timestamp > cutoff).ToList();
                    if (filteredHistory.Any())
                    {
                        _queryHistory[kvp.Key] = filteredHistory;
                    }
                    else
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _queryHistory.TryRemove(key, out _);
                }

                _logger.LogDebug("Cleaned up old query metrics, removed {Count} expired history keys", keysToRemove.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during metrics cleanup");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _metricsCleanupTimer?.Dispose();
            }
        }
    }

    /// <summary>
    /// Internal query metric for tracking performance.
    /// </summary>
    internal class QueryMetric
    {
        public string ServerId { get; set; }
        public string ResourceType { get; set; }
        public long ExecutionTimeMs { get; set; }
        public int ResultCount { get; set; }
        public bool WasSuccessful { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string QueryHash { get; set; }
    }
}
