// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Configuration
{
    /// <summary>
    /// Service for validating FanoutBroker configuration settings.
    /// </summary>
    public interface IConfigurationValidationService
    {
        /// <summary>
        /// Validates the FanoutBroker configuration for correctness and consistency.
        /// </summary>
        /// <param name="configuration">The configuration to validate.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Configuration validation result.</returns>
        Task<ConfigurationValidationResult> ValidateConfigurationAsync(
            FanoutBrokerConfiguration configuration,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates that configured FHIR servers are reachable and compatible.
        /// </summary>
        /// <param name="configuration">The configuration to validate.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Server connectivity validation result.</returns>
        Task<ServerConnectivityValidationResult> ValidateServerConnectivityAsync(
            FanoutBrokerConfiguration configuration,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Result of configuration validation.
    /// </summary>
    public class ConfigurationValidationResult
    {
        /// <summary>
        /// Whether the configuration is valid.
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// List of validation errors, if any.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists", Justification = "Model class requires List<T> for simple string collection")]
        [SuppressMessage("Microsoft.Design", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Model class requires setter for initialization")]
        public List<string> ValidationErrors { get; set; } = new List<string>();

        /// <summary>
        /// List of validation warnings, if any.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists", Justification = "Model class requires List<T> for simple string collection")]
        [SuppressMessage("Microsoft.Design", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Model class requires setter for initialization")]
        public List<string> ValidationWarnings { get; set; } = new List<string>();

        /// <summary>
        /// Summary of validation findings.
        /// </summary>
        public string Summary { get; set; }
    }

    /// <summary>
    /// Result of server connectivity validation.
    /// </summary>
    public class ServerConnectivityValidationResult
    {
        /// <summary>
        /// Whether all configured servers are reachable.
        /// </summary>
        public bool AllServersReachable { get; set; }

        /// <summary>
        /// Number of reachable servers.
        /// </summary>
        public int ReachableServerCount { get; set; }

        /// <summary>
        /// Total number of configured servers.
        /// </summary>
        public int TotalServerCount { get; set; }

        /// <summary>
        /// Detailed results for each server.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists", Justification = "Model class requires List<T> for simple validation results")]
        [SuppressMessage("Microsoft.Design", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Model class requires setter for initialization")]
        public List<ServerValidationDetail> ServerDetails { get; set; } = new List<ServerValidationDetail>();

        /// <summary>
        /// Summary of connectivity validation.
        /// </summary>
        public string Summary { get; set; }
    }

    /// <summary>
    /// Detailed validation result for a single server.
    /// </summary>
    public class ServerValidationDetail
    {
        /// <summary>
        /// Server identifier.
        /// </summary>
        public string ServerId { get; set; }

        /// <summary>
        /// Server name.
        /// </summary>
        public string ServerName { get; set; }

        /// <summary>
        /// Server base URL.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1056:UriPropertiesShouldNotBeStrings", Justification = "Model property needs string for validation results display")]
        public string BaseUrl { get; set; }

        /// <summary>
        /// Whether the server is reachable.
        /// </summary>
        public bool IsReachable { get; set; }

        /// <summary>
        /// Response time in milliseconds.
        /// </summary>
        public long ResponseTimeMs { get; set; }

        /// <summary>
        /// FHIR version reported by the server.
        /// </summary>
        public string FhirVersion { get; set; }

        /// <summary>
        /// Server software name and version.
        /// </summary>
        public string ServerSoftware { get; set; }

        /// <summary>
        /// Any validation errors for this server.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists", Justification = "Model class requires List<T> for simple string collection")]
        [SuppressMessage("Microsoft.Design", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Model class requires setter for initialization")]
        public List<string> ValidationErrors { get; set; } = new List<string>();

        /// <summary>
        /// Any validation warnings for this server.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists", Justification = "Model class requires List<T> for simple string collection")]
        [SuppressMessage("Microsoft.Design", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Model class requires setter for initialization")]
        public List<string> ValidationWarnings { get; set; } = new List<string>();
    }
}