// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;

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