// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Health
{
    /// <summary>
    /// Health check for the Fanout Broker service that verifies connectivity to target FHIR servers.
    /// </summary>
    public class FanoutBrokerHealthCheck : IHealthCheck
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly ILogger<FanoutBrokerHealthCheck> _logger;

        public FanoutBrokerHealthCheck(
            IFhirServerOrchestrator serverOrchestrator,
            ILogger<FanoutBrokerHealthCheck> logger)
        {
            _serverOrchestrator = serverOrchestrator ?? throw new ArgumentNullException(nameof(serverOrchestrator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var enabledServers = _serverOrchestrator.GetEnabledServers();
                
                if (!enabledServers.Any())
                {
                    return HealthCheckResult.Unhealthy(
                        "No FHIR servers configured or enabled",
                        data: new Dictionary<string, object>
                        {
                            ["ConfiguredServers"] = 0,
                            ["EnabledServers"] = 0
                        });
                }

                var healthTasks = enabledServers.Select(server => 
                    _serverOrchestrator.CheckHealthAsync(server, cancellationToken));
                
                var healthResults = await Task.WhenAll(healthTasks);
                
                var healthyServers = healthResults.Count(r => r.IsHealthy);
                var totalServers = healthResults.Length;
                
                var data = new Dictionary<string, object>
                {
                    ["TotalServers"] = totalServers,
                    ["HealthyServers"] = healthyServers,
                    ["UnhealthyServers"] = totalServers - healthyServers,
                    ["ServersDetail"] = healthResults.Select(r => new
                    {
                        ServerId = r.ServerId,
                        IsHealthy = r.IsHealthy,
                        ResponseTimeMs = r.ResponseTimeMs,
                        ErrorMessage = r.ErrorMessage
                    }).ToList()
                };

                // Health status logic
                if (healthyServers == totalServers)
                {
                    return HealthCheckResult.Healthy(
                        $"All {totalServers} FHIR servers are healthy",
                        data);
                }
                else if (healthyServers > 0)
                {
                    return HealthCheckResult.Degraded(
                        $"{healthyServers} of {totalServers} FHIR servers are healthy",
                        data: data);
                }
                else
                {
                    return HealthCheckResult.Unhealthy(
                        "No FHIR servers are healthy",
                        data: data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check");
                
                return HealthCheckResult.Unhealthy(
                    "Health check failed due to an error",
                    exception: ex,
                    data: new Dictionary<string, object>
                    {
                        ["ErrorMessage"] = ex.Message
                    });
            }
        }
    }
}