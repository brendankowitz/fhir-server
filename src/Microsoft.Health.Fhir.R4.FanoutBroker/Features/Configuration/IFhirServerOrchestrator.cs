// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Configuration
{
    /// <summary>
    /// Orchestrates FHIR client operations across multiple server endpoints.
    /// </summary>
    public interface IFhirServerOrchestrator
    {
        /// <summary>
        /// Gets all enabled FHIR server endpoints.
        /// </summary>
        /// <returns>List of enabled FHIR server endpoints.</returns>
        IReadOnlyList<FhirServerEndpoint> GetEnabledServers();

        /// <summary>
        /// Executes a search request against a specific FHIR server.
        /// </summary>
        /// <param name="server">The FHIR server endpoint to search.</param>
        /// <param name="searchOptions">The search options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Server search result containing the response or error information.</returns>
        Task<ServerSearchResult> SearchAsync(
            FhirServerEndpoint server, 
            SearchOptions searchOptions, 
            CancellationToken cancellationToken);

        /// <summary>
        /// Checks the health status of a specific FHIR server.
        /// </summary>
        /// <param name="server">The FHIR server endpoint to check.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Health check result.</returns>
        Task<ServerHealthResult> CheckHealthAsync(
            FhirServerEndpoint server, 
            CancellationToken cancellationToken);

        /// <summary>
        /// Gets the capability statement from a specific FHIR server.
        /// </summary>
        /// <param name="server">The FHIR server endpoint.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Server capability result.</returns>
        Task<ServerCapabilityResult> GetCapabilityStatementAsync(
            FhirServerEndpoint server, 
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Result of a search operation against a specific FHIR server.
    /// </summary>
    public class ServerSearchResult
    {
        /// <summary>
        /// Server identifier.
        /// </summary>
        public string ServerId { get; set; }

        /// <summary>
        /// Server base URL.
        /// </summary>
        public string ServerBaseUrl { get; set; }

        /// <summary>
        /// Whether the search was successful.
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// The search result from the server.
        /// </summary>
        public SearchResult SearchResult { get; set; }

        /// <summary>
        /// Error message if the search failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Response time in milliseconds.
        /// </summary>
        public long ResponseTimeMs { get; set; }

        /// <summary>
        /// HTTP status code returned by the server.
        /// </summary>
        public int? StatusCode { get; set; }
    }

    /// <summary>
    /// Result of a health check operation against a specific FHIR server.
    /// </summary>
    public class ServerHealthResult
    {
        /// <summary>
        /// Server identifier.
        /// </summary>
        public string ServerId { get; set; }

        /// <summary>
        /// Whether the server is healthy.
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// Error message if the health check failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Response time in milliseconds.
        /// </summary>
        public long ResponseTimeMs { get; set; }

        /// <summary>
        /// HTTP status code returned by the server.
        /// </summary>
        public int? StatusCode { get; set; }
    }

    /// <summary>
    /// Result of a capability statement request against a specific FHIR server.
    /// </summary>
    public class ServerCapabilityResult
    {
        /// <summary>
        /// Server identifier.
        /// </summary>
        public string ServerId { get; set; }

        /// <summary>
        /// Whether the capability statement was successfully retrieved.
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// The capability statement from the server.
        /// </summary>
        public Hl7.Fhir.Model.CapabilityStatement CapabilityStatement { get; set; }

        /// <summary>
        /// Error message if the request failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Response time in milliseconds.
        /// </summary>
        public long ResponseTimeMs { get; set; }

        /// <summary>
        /// HTTP status code returned by the server.
        /// </summary>
        public int? StatusCode { get; set; }
    }
}