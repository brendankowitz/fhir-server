// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Configuration
{
    /// <summary>
    /// Implementation of configuration validation service.
    /// </summary>
    public class ConfigurationValidationService : IConfigurationValidationService
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly ILogger<ConfigurationValidationService> _logger;

        public ConfigurationValidationService(
            IFhirServerOrchestrator serverOrchestrator,
            ILogger<ConfigurationValidationService> logger)
        {
            _serverOrchestrator = serverOrchestrator ?? throw new ArgumentNullException(nameof(serverOrchestrator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<ConfigurationValidationResult> ValidateConfigurationAsync(
            FanoutBrokerConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            var result = new ConfigurationValidationResult();

            try
            {
                // Validate server configuration
                ValidateServerConfiguration(configuration, result);

                // Validate timeout settings
                ValidateTimeoutSettings(configuration, result);

                // Validate resource limits
                ValidateResourceLimits(configuration, result);

                // Validate circuit breaker settings
                ValidateCircuitBreakerSettings(configuration, result);

                // Set overall validity
                result.IsValid = !result.ValidationErrors.Any();

                // Generate summary
                if (result.IsValid)
                {
                    result.Summary = $"Configuration is valid. Found {result.ValidationWarnings.Count} warnings.";
                }
                else
                {
                    result.Summary = $"Configuration is invalid. Found {result.ValidationErrors.Count} errors and {result.ValidationWarnings.Count} warnings.";
                }

                _logger.LogInformation("Configuration validation completed: {Summary}", result.Summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during configuration validation");
                result.IsValid = false;
                result.ValidationErrors.Add($"Configuration validation failed: {ex.Message}");
                result.Summary = "Configuration validation failed due to an unexpected error.";
            }

            return Task.FromResult(result);
        }

        public async Task<ServerConnectivityValidationResult> ValidateServerConnectivityAsync(
            FanoutBrokerConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            var result = new ServerConnectivityValidationResult
            {
                TotalServerCount = configuration.FhirServers?.Count ?? 0
            };

            try
            {
                if (configuration.FhirServers == null || !configuration.FhirServers.Any())
                {
                    result.Summary = "No FHIR servers configured.";
                    return result;
                }

                var validationTasks = configuration.FhirServers.Select(server =>
                    ValidateServerConnectivityAsync(server, cancellationToken));

                var serverDetails = await Task.WhenAll(validationTasks);
                result.ServerDetails.AddRange(serverDetails);

                result.ReachableServerCount = serverDetails.Count(d => d.IsReachable);
                result.AllServersReachable = result.ReachableServerCount == result.TotalServerCount;

                result.Summary = result.AllServersReachable
                    ? $"All {result.TotalServerCount} servers are reachable."
                    : $"{result.ReachableServerCount} of {result.TotalServerCount} servers are reachable.";

                _logger.LogInformation("Server connectivity validation completed: {Summary}", result.Summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during server connectivity validation");
                result.Summary = "Server connectivity validation failed due to an unexpected error.";
            }

            return result;
        }

        private void ValidateServerConfiguration(FanoutBrokerConfiguration configuration, ConfigurationValidationResult result)
        {
            if (configuration.FhirServers == null || !configuration.FhirServers.Any())
            {
                result.ValidationErrors.Add("No FHIR servers configured. At least one server must be configured.");
                return;
            }

            // Check for duplicate server IDs
            var serverIds = configuration.FhirServers.Select(s => s.Id).ToList();
            var duplicateIds = serverIds.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key);
            foreach (var duplicateId in duplicateIds)
            {
                result.ValidationErrors.Add($"Duplicate server ID found: {duplicateId}");
            }

            // Validate individual servers
            foreach (var server in configuration.FhirServers)
            {
                ValidateServerEndpoint(server, result);
            }

            // Check if at least one server is enabled
            if (!configuration.FhirServers.Any(s => s.IsEnabled))
            {
                result.ValidationWarnings.Add("No servers are enabled. The fanout broker will not be able to handle requests.");
            }
        }

        private void ValidateServerEndpoint(FhirServerEndpoint server, ConfigurationValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(server.Id))
            {
                result.ValidationErrors.Add("Server ID cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(server.BaseUrl))
            {
                result.ValidationErrors.Add($"Server '{server.Id}' has no base URL configured.");
            }
            else if (!Uri.TryCreate(server.BaseUrl, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                result.ValidationErrors.Add($"Server '{server.Id}' has invalid base URL: {server.BaseUrl}");
            }

            if (server.Priority < 1)
            {
                result.ValidationWarnings.Add($"Server '{server.Id}' has priority {server.Priority}. Priority should be 1 or higher.");
            }

            if (server.TimeoutSeconds.HasValue && server.TimeoutSeconds.Value <= 0)
            {
                result.ValidationErrors.Add($"Server '{server.Id}' has invalid timeout: {server.TimeoutSeconds}. Timeout must be positive.");
            }

            // Validate authentication configuration
            ValidateServerAuthentication(server, result);
        }

        private void ValidateServerAuthentication(FhirServerEndpoint server, ConfigurationValidationResult result)
        {
            if (server.Authentication == null)
            {
                return; // No authentication is valid
            }

            switch (server.Authentication.Type)
            {
                case AuthenticationType.Bearer:
                    if (string.IsNullOrWhiteSpace(server.Authentication.BearerToken))
                    {
                        result.ValidationErrors.Add($"Server '{server.Id}' uses Bearer authentication but no token is configured.");
                    }
                    break;

                case AuthenticationType.Basic:
                    if (string.IsNullOrWhiteSpace(server.Authentication.Username) ||
                        string.IsNullOrWhiteSpace(server.Authentication.Password))
                    {
                        result.ValidationErrors.Add($"Server '{server.Id}' uses Basic authentication but username or password is missing.");
                    }
                    break;

                case AuthenticationType.ClientCredentials:
                    if (string.IsNullOrWhiteSpace(server.Authentication.ClientId) ||
                        string.IsNullOrWhiteSpace(server.Authentication.ClientSecret) ||
                        string.IsNullOrWhiteSpace(server.Authentication.TokenEndpoint))
                    {
                        result.ValidationErrors.Add($"Server '{server.Id}' uses ClientCredentials authentication but required fields are missing.");
                    }
                    else if (!Uri.TryCreate(server.Authentication.TokenEndpoint, UriKind.Absolute, out _))
                    {
                        result.ValidationErrors.Add($"Server '{server.Id}' has invalid token endpoint URL: {server.Authentication.TokenEndpoint}");
                    }
                    break;
            }
        }

        private void ValidateTimeoutSettings(FanoutBrokerConfiguration configuration, ConfigurationValidationResult result)
        {
            if (configuration.SearchTimeoutSeconds <= 0)
            {
                result.ValidationErrors.Add($"SearchTimeoutSeconds must be positive. Current value: {configuration.SearchTimeoutSeconds}");
            }

            if (configuration.ChainSearchTimeoutSeconds <= 0)
            {
                result.ValidationErrors.Add($"ChainSearchTimeoutSeconds must be positive. Current value: {configuration.ChainSearchTimeoutSeconds}");
            }

            if (configuration.QueryTimeoutSeconds <= 0)
            {
                result.ValidationErrors.Add($"QueryTimeoutSeconds must be positive. Current value: {configuration.QueryTimeoutSeconds}");
            }

            if (configuration.ChainSearchTimeoutSeconds >= configuration.SearchTimeoutSeconds)
            {
                result.ValidationWarnings.Add("ChainSearchTimeoutSeconds should be less than SearchTimeoutSeconds to allow proper timeout handling.");
            }
        }

        private void ValidateResourceLimits(FanoutBrokerConfiguration configuration, ConfigurationValidationResult result)
        {
            if (configuration.MaxResultsPerServer <= 0)
            {
                result.ValidationErrors.Add($"MaxResultsPerServer must be positive. Current value: {configuration.MaxResultsPerServer}");
            }

            if (configuration.MaxTotalResults <= 0)
            {
                result.ValidationErrors.Add($"MaxTotalResults must be positive. Current value: {configuration.MaxTotalResults}");
            }

            if (configuration.MaxTotalResults < configuration.MaxResultsPerServer)
            {
                result.ValidationWarnings.Add("MaxTotalResults is less than MaxResultsPerServer, which may limit results unnecessarily.");
            }

            if (configuration.MaxChainDepth <= 0)
            {
                result.ValidationErrors.Add($"MaxChainDepth must be positive. Current value: {configuration.MaxChainDepth}");
            }

            if (configuration.MaxIncludedResourcesInBundle <= 0)
            {
                result.ValidationErrors.Add($"MaxIncludedResourcesInBundle must be positive. Current value: {configuration.MaxIncludedResourcesInBundle}");
            }

            if (configuration.MaxMemoryUsageMB <= 0)
            {
                result.ValidationErrors.Add($"MaxMemoryUsageMB must be positive. Current value: {configuration.MaxMemoryUsageMB}");
            }

            if (configuration.MaxConcurrentSearches <= 0)
            {
                result.ValidationErrors.Add($"MaxConcurrentSearches must be positive. Current value: {configuration.MaxConcurrentSearches}");
            }

            if (configuration.MaxResourceSizeKB <= 0)
            {
                result.ValidationErrors.Add($"MaxResourceSizeKB must be positive. Current value: {configuration.MaxResourceSizeKB}");
            }

            if (configuration.MaxParallelServers <= 0)
            {
                result.ValidationErrors.Add($"MaxParallelServers must be positive. Current value: {configuration.MaxParallelServers}");
            }

            if (configuration.MaxQueriesPerMinute <= 0)
            {
                result.ValidationErrors.Add($"MaxQueriesPerMinute must be positive. Current value: {configuration.MaxQueriesPerMinute}");
            }
        }

        private void ValidateCircuitBreakerSettings(FanoutBrokerConfiguration configuration, ConfigurationValidationResult result)
        {
            if (configuration.EnableCircuitBreaker)
            {
                if (configuration.CircuitBreakerFailureThreshold <= 0)
                {
                    result.ValidationErrors.Add($"CircuitBreakerFailureThreshold must be positive when circuit breaker is enabled. Current value: {configuration.CircuitBreakerFailureThreshold}");
                }

                if (configuration.CircuitBreakerTimeoutSeconds <= 0)
                {
                    result.ValidationErrors.Add($"CircuitBreakerTimeoutSeconds must be positive when circuit breaker is enabled. Current value: {configuration.CircuitBreakerTimeoutSeconds}");
                }
            }

            if (configuration.FillFactor <= 0 || configuration.FillFactor > 1)
            {
                result.ValidationErrors.Add($"FillFactor must be between 0 and 1. Current value: {configuration.FillFactor}");
            }
        }

        private async Task<ServerValidationDetail> ValidateServerConnectivityAsync(
            FhirServerEndpoint server,
            CancellationToken cancellationToken)
        {
            var detail = new ServerValidationDetail
            {
                ServerId = server.Id,
                ServerName = server.Name,
                BaseUrl = server.BaseUrl
            };

            try
            {
                if (!server.IsEnabled)
                {
                    detail.ValidationWarnings.Add("Server is disabled.");
                    return detail;
                }

                var healthResult = await _serverOrchestrator.CheckHealthAsync(server, cancellationToken);
                detail.IsReachable = healthResult.IsHealthy;
                detail.ResponseTimeMs = healthResult.ResponseTimeMs;

                if (!detail.IsReachable)
                {
                    detail.ValidationErrors.Add($"Server is not reachable: {healthResult.ErrorMessage}");
                }

                // Get capability statement for additional validation
                var capabilityResult = await _serverOrchestrator.GetCapabilityStatementAsync(server, cancellationToken);
                if (capabilityResult.IsSuccess && capabilityResult.CapabilityStatement != null)
                {
                    // Extract server information from capability statement
                    detail.FhirVersion = capabilityResult.CapabilityStatement.FhirVersion?.ToString();
                    detail.ServerSoftware = capabilityResult.CapabilityStatement.Software?.Name;

                    if (string.IsNullOrEmpty(detail.FhirVersion))
                    {
                        detail.ValidationWarnings.Add("Server did not report FHIR version in capability statement.");
                    }
                    else if (!detail.FhirVersion.StartsWith("4.", StringComparison.OrdinalIgnoreCase))
                    {
                        detail.ValidationWarnings.Add($"Server reports FHIR version {detail.FhirVersion}, but this fanout broker is designed for FHIR R4.");
                    }
                }
                else if (detail.IsReachable)
                {
                    detail.ValidationWarnings.Add("Server is reachable but capability statement could not be retrieved.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error validating server connectivity for {ServerId}", server.Id);
                detail.IsReachable = false;
                detail.ValidationErrors.Add($"Connectivity validation failed: {ex.Message}");
            }

            return detail;
        }
    }
}
