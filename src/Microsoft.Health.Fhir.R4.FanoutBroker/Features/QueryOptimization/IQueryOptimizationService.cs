// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.QueryOptimization
{
    /// <summary>
    /// Service for optimizing query distribution across multiple FHIR servers
    /// based on server capabilities, performance metrics, and query cost analysis.
    /// </summary>
    public interface IQueryOptimizationService
    {
        /// <summary>
        /// Analyzes a search query and estimates the cost of executing it.
        /// </summary>
        /// <param name="searchOptions">The search options to analyze.</param>
        /// <param name="resourceType">The resource type being searched.</param>
        /// <returns>Query cost analysis result.</returns>
        Task<QueryCostAnalysis> AnalyzeQueryCostAsync(SearchOptions searchOptions, string resourceType);

        /// <summary>
        /// Optimizes server selection based on query characteristics and server capabilities.
        /// </summary>
        /// <param name="availableServers">List of available servers.</param>
        /// <param name="searchOptions">The search options.</param>
        /// <param name="resourceType">The resource type being searched.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Optimized list of servers to query with execution plan.</returns>
        Task<QueryExecutionPlan> OptimizeServerSelectionAsync(
            IReadOnlyList<FhirServerEndpoint> availableServers,
            SearchOptions searchOptions,
            string resourceType,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Records query execution metrics for future optimization decisions.
        /// </summary>
        /// <param name="serverId">The server that executed the query.</param>
        /// <param name="searchOptions">The search options that were executed.</param>
        /// <param name="resourceType">The resource type that was searched.</param>
        /// <param name="executionTimeMs">Execution time in milliseconds.</param>
        /// <param name="resultCount">Number of results returned.</param>
        /// <param name="wasSuccessful">Whether the query was successful.</param>
        Task RecordQueryMetricsAsync(
            string serverId,
            SearchOptions searchOptions,
            string resourceType,
            long executionTimeMs,
            int resultCount,
            bool wasSuccessful);

        /// <summary>
        /// Gets performance metrics for a specific server.
        /// </summary>
        /// <param name="serverId">The server ID.</param>
        /// <returns>Server performance metrics.</returns>
        Task<ServerPerformanceMetrics> GetServerPerformanceAsync(string serverId);

        /// <summary>
        /// Determines if a query should use parallel execution across multiple servers
        /// or if it should be optimized for a specific server.
        /// </summary>
        /// <param name="searchOptions">The search options.</param>
        /// <param name="resourceType">The resource type being searched.</param>
        /// <param name="availableServers">Available servers.</param>
        /// <returns>Execution strategy recommendation.</returns>
        Task<QueryExecutionStrategy> RecommendExecutionStrategyAsync(
            SearchOptions searchOptions,
            string resourceType,
            IReadOnlyList<FhirServerEndpoint> availableServers);
    }

    /// <summary>
    /// Result of query cost analysis.
    /// </summary>
    public class QueryCostAnalysis
    {
        /// <summary>
        /// Estimated relative cost of the query (1-100, where 100 is most expensive).
        /// </summary>
        public int EstimatedCost { get; set; }

        /// <summary>
        /// Estimated execution time in milliseconds.
        /// </summary>
        public int EstimatedExecutionTimeMs { get; set; }

        /// <summary>
        /// Estimated number of results.
        /// </summary>
        public int EstimatedResultCount { get; set; }

        /// <summary>
        /// Factors that contribute to the query cost.
        /// </summary>
        public List<string> CostFactors { get; set; } = new List<string>();

        /// <summary>
        /// Whether the query is considered expensive and should be throttled.
        /// </summary>
        public bool IsExpensiveQuery { get; set; }

        /// <summary>
        /// Optimization recommendations.
        /// </summary>
        public List<string> OptimizationRecommendations { get; set; } = new List<string>();
    }

    /// <summary>
    /// Query execution plan with optimized server selection.
    /// </summary>
    public class QueryExecutionPlan
    {
        /// <summary>
        /// Servers selected for execution, ordered by preference.
        /// </summary>
        public List<FhirServerEndpoint> SelectedServers { get; set; } = new List<FhirServerEndpoint>();

        /// <summary>
        /// Servers excluded from execution and reasons why.
        /// </summary>
        public List<ExcludedServer> ExcludedServers { get; set; } = new List<ExcludedServer>();

        /// <summary>
        /// Recommended execution strategy.
        /// </summary>
        public QueryExecutionStrategy ExecutionStrategy { get; set; }

        /// <summary>
        /// Estimated total execution time across all servers.
        /// </summary>
        public int EstimatedTotalTimeMs { get; set; }

        /// <summary>
        /// Confidence level in the optimization (0-100).
        /// </summary>
        public int ConfidenceLevel { get; set; }

        /// <summary>
        /// Reasoning behind the optimization decisions.
        /// </summary>
        public List<string> OptimizationReasoning { get; set; } = new List<string>();
    }

    /// <summary>
    /// Server excluded from execution.
    /// </summary>
    public class ExcludedServer
    {
        /// <summary>
        /// The excluded server.
        /// </summary>
        public FhirServerEndpoint Server { get; set; }

        /// <summary>
        /// Reason for exclusion.
        /// </summary>
        public string ExclusionReason { get; set; }
    }

    /// <summary>
    /// Server performance metrics for optimization decisions.
    /// </summary>
    public class ServerPerformanceMetrics
    {
        /// <summary>
        /// Server identifier.
        /// </summary>
        public string ServerId { get; set; }

        /// <summary>
        /// Average response time in milliseconds.
        /// </summary>
        public double AverageResponseTimeMs { get; set; }

        /// <summary>
        /// Success rate (0-1).
        /// </summary>
        public double SuccessRate { get; set; }

        /// <summary>
        /// Total queries executed.
        /// </summary>
        public long TotalQueries { get; set; }

        /// <summary>
        /// Average results per query.
        /// </summary>
        public double AverageResultsPerQuery { get; set; }

        /// <summary>
        /// Current health status.
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// Performance rating (1-10, where 10 is best).
        /// </summary>
        public int PerformanceRating { get; set; }

        /// <summary>
        /// Resource types this server handles well.
        /// </summary>
        public List<string> OptimalResourceTypes { get; set; } = new List<string>();

        /// <summary>
        /// Last updated timestamp.
        /// </summary>
        public DateTimeOffset LastUpdated { get; set; }
    }

    /// <summary>
    /// Query execution strategy options.
    /// </summary>
    public enum QueryExecutionStrategy
    {
        /// <summary>
        /// Execute on all available servers in parallel.
        /// </summary>
        ParallelAll,

        /// <summary>
        /// Execute on a subset of optimal servers in parallel.
        /// </summary>
        ParallelOptimal,

        /// <summary>
        /// Execute on the single best server.
        /// </summary>
        SingleBest,

        /// <summary>
        /// Execute sequentially with fallback.
        /// </summary>
        SequentialFallback,

        /// <summary>
        /// Route to specific servers based on query characteristics.
        /// </summary>
        RoutedByQuery
    }
}