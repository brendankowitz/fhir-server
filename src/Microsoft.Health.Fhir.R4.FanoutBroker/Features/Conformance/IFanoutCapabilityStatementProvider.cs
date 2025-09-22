// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Conformance
{
    /// <summary>
    /// Provides capability statements for the fanout broker service by computing the intersection
    /// of capabilities across multiple FHIR servers.
    /// </summary>
    public interface IFanoutCapabilityStatementProvider
    {
        /// <summary>
        /// Gets the aggregated capability statement representing the intersection of capabilities
        /// from all enabled FHIR servers.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Capability statement showing only features supported by all servers.</returns>
        Task<CapabilityStatement> GetCapabilityStatementAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets capability statements from all enabled servers for comparison and diagnostics.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Dictionary of server ID to capability statement.</returns>
        Task<IDictionary<string, ServerCapabilityResult>> GetServerCapabilitiesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates if a search parameter is supported by all servers.
        /// </summary>
        /// <param name="resourceType">The resource type (e.g., "Patient").</param>
        /// <param name="searchParameter">The search parameter name (e.g., "name").</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if the search parameter is supported by all servers.</returns>
        Task<bool> IsSearchParameterSupportedAsync(string resourceType, string searchParameter, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the list of resource types supported by all servers.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>List of resource types supported by all servers.</returns>
        Task<IReadOnlyList<string>> GetSupportedResourceTypesAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Result of retrieving capability statement from a server.
    /// </summary>
    public class ServerCapabilityResult
    {
        /// <summary>
        /// Server identifier.
        /// </summary>
        public string ServerId { get; set; }

        /// <summary>
        /// Server base URL.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1056:UriPropertiesShouldNotBeStrings", Justification = "Model property needs string for result display")]
        public string ServerBaseUrl { get; set; }

        /// <summary>
        /// Whether the capability statement was successfully retrieved.
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// The capability statement if successfully retrieved.
        /// </summary>
        public CapabilityStatement CapabilityStatement { get; set; }

        /// <summary>
        /// Error message if retrieval failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Response time in milliseconds.
        /// </summary>
        public long ResponseTimeMs { get; set; }
    }
}