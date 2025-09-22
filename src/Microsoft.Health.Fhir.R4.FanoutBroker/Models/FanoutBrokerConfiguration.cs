// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;

namespace Microsoft.Health.Fhir.FanoutBroker.Models
{
    /// <summary>
    /// Configuration for the Fanout Broker service.
    /// </summary>
    public class FanoutBrokerConfiguration
    {
        /// <summary>
        /// List of target FHIR servers to fan out queries to.
        /// </summary>
        public List<FhirServerEndpoint> FhirServers { get; set; } = new List<FhirServerEndpoint>();

        /// <summary>
        /// Default timeout for search operations in seconds.
        /// </summary>
        public int SearchTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Timeout for chained search sub-queries in seconds.
        /// </summary>
        public int ChainSearchTimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// Fill factor threshold for sequential execution (default 0.5 = 50%).
        /// </summary>
        public double FillFactor { get; set; } = 0.5;

        /// <summary>
        /// Maximum number of results to return from a single server.
        /// </summary>
        public int MaxResultsPerServer { get; set; } = 1000;

        /// <summary>
        /// Maximum chain depth allowed for chained search expressions.
        /// </summary>
        public int MaxChainDepth { get; set; } = 3;

        /// <summary>
        /// Maximum number of included resources to include in the main search bundle.
        /// If exceeded, a related link to $includes operation is provided.
        /// </summary>
        public int MaxIncludedResourcesInBundle { get; set; } = 1000;

        /// <summary>
        /// Whether to enable circuit breaker pattern for server failures.
        /// </summary>
        public bool EnableCircuitBreaker { get; set; } = true;

        /// <summary>
        /// Circuit breaker failure threshold before opening the circuit.
        /// </summary>
        public int CircuitBreakerFailureThreshold { get; set; } = 5;

        /// <summary>
        /// Circuit breaker timeout in seconds before attempting to close the circuit.
        /// </summary>
        public int CircuitBreakerTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Maximum total number of results across all servers for a single query.
        /// </summary>
        public int MaxTotalResults { get; set; } = 10000;

        /// <summary>
        /// Maximum memory usage in MB before throttling queries.
        /// </summary>
        public int MaxMemoryUsageMB { get; set; } = 500;

        /// <summary>
        /// Maximum concurrent search requests to all servers.
        /// </summary>
        public int MaxConcurrentSearches { get; set; } = 20;

        /// <summary>
        /// Maximum size of a single resource in KB.
        /// </summary>
        public int MaxResourceSizeKB { get; set; } = 1024;

        /// <summary>
        /// Query timeout in seconds for individual server requests.
        /// </summary>
        public int QueryTimeoutSeconds { get; set; } = 45;

        /// <summary>
        /// Maximum number of servers that can be queried in parallel.
        /// </summary>
        public int MaxParallelServers { get; set; } = 10;

        /// <summary>
        /// Rate limit: maximum queries per minute from a single client.
        /// </summary>
        public int MaxQueriesPerMinute { get; set; } = 60;

        /// <summary>
        /// Enable memory monitoring and resource protection.
        /// </summary>
        public bool EnableResourceProtection { get; set; } = true;

        /// <summary>
        /// Resolution strategy for chained search expressions.
        /// Passthrough: Assumes references are co-located on same servers (optimal performance).
        /// Distributed: Executes comprehensive cross-shard resolution (higher latency, complete coverage).
        /// </summary>
        public ResolutionMode ChainedSearchResolution { get; set; } = ResolutionMode.Passthrough;

        /// <summary>
        /// Resolution strategy for include/revinclude operations.
        /// Passthrough: Assumes included resources are co-located (optimal performance).
        /// Distributed: Executes comprehensive cross-shard resolution (higher latency, complete coverage).
        /// </summary>
        public ResolutionMode IncludeResolution { get; set; } = ResolutionMode.Passthrough;

        /// <summary>
        /// Timeout for distributed chain resolution phases in seconds.
        /// </summary>
        public int DistributedChainTimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// Timeout for distributed include resolution phases in seconds.
        /// </summary>
        public int DistributedIncludeTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Maximum number of reference IDs to process in distributed resolution mode.
        /// </summary>
        public int MaxDistributedReferenceIds { get; set; } = 1000;

        /// <summary>
        /// Batch size for ID-based queries in distributed resolution mode.
        /// </summary>
        public int DistributedBatchSize { get; set; } = 100;

        /// <summary>
        /// Enable caching for distributed resolution results to optimize repeated sub-queries.
        /// </summary>
        public bool EnableDistributedResolutionCache { get; set; } = true;

        /// <summary>
        /// Cache expiration time in minutes for distributed resolution results.
        /// </summary>
        public int DistributedResolutionCacheExpirationMinutes { get; set; } = 5;
    }

    /// <summary>
    /// Configuration for a single FHIR server endpoint.
    /// </summary>
    public class FhirServerEndpoint
    {
        /// <summary>
        /// Unique identifier for this server endpoint.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Display name for this server endpoint.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Base URL of the FHIR server.
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// Authentication configuration for this server.
        /// </summary>
        public FhirServerAuthentication Authentication { get; set; }

        /// <summary>
        /// Whether this server is enabled for queries.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Priority weight for this server (higher priority servers are queried first in sequential execution).
        /// </summary>
        public int Priority { get; set; } = 1;

        /// <summary>
        /// Timeout specific to this server in seconds (overrides global timeout if set).
        /// </summary>
        public int? TimeoutSeconds { get; set; }

        /// <summary>
        /// Additional HTTP headers to send to this server.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Authentication configuration for a FHIR server endpoint.
    /// </summary>
    public class FhirServerAuthentication
    {
        /// <summary>
        /// Authentication type (None, Bearer, Basic, ClientCredentials).
        /// </summary>
        public AuthenticationType Type { get; set; } = AuthenticationType.None;

        /// <summary>
        /// Bearer token for Bearer authentication.
        /// </summary>
        public string BearerToken { get; set; }

        /// <summary>
        /// Username for Basic authentication.
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Password for Basic authentication.
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// Client ID for OAuth2 client credentials flow.
        /// </summary>
        public string ClientId { get; set; }

        /// <summary>
        /// Client secret for OAuth2 client credentials flow.
        /// </summary>
        public string ClientSecret { get; set; }

        /// <summary>
        /// Token endpoint for OAuth2 client credentials flow.
        /// </summary>
        public string TokenEndpoint { get; set; }

        /// <summary>
        /// Scope for OAuth2 client credentials flow.
        /// </summary>
        public string Scope { get; set; }
    }

    /// <summary>
    /// Supported authentication types for FHIR server endpoints.
    /// </summary>
    public enum AuthenticationType
    {
        None,
        Bearer,
        Basic,
        ClientCredentials
    }
}