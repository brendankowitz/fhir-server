// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Conformance
{
    /// <summary>
    /// Provides capability statement for the fanout broker service by intersecting
    /// capabilities from target FHIR servers.
    /// </summary>
    public class FanoutCapabilityStatementProvider : IProvideCapability, IConformanceProvider
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<FanoutCapabilityStatementProvider> _logger;
        private readonly CapabilityStatement _cachedCapabilityStatement;
        private readonly object _cacheLock = new object();
        private DateTime _cacheExpiry = DateTime.MinValue;
        private readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(10);

        public FanoutCapabilityStatementProvider(
            IFhirServerOrchestrator serverOrchestrator,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<FanoutCapabilityStatementProvider> logger)
        {
            _serverOrchestrator = serverOrchestrator ?? throw new ArgumentNullException(nameof(serverOrchestrator));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public void Build(ICapabilityStatementBuilder builder)
        {
            // This method is called during service registration to configure capabilities
            // For fanout broker, we'll configure basic read-only search capabilities
            
            builder
                .AddGlobalSearchParameters()
                .SyncSearchParametersAsync()
                .SyncProfiles();

            // Configure server information
            builder.Update(capability =>
            {
                capability.Name = "FHIR Fanout Broker";
                capability.Title = "FHIR Fanout Broker Query Service";
                capability.Description = new Markdown("Read-only FHIR service that aggregates search queries across multiple FHIR servers");
                capability.Kind = CapabilityStatement.CapabilityStatementKind.Instance;
                capability.Status = PublicationStatus.Active;
                capability.Date = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssK");
                capability.Software = new CapabilityStatement.SoftwareComponent
                {
                    Name = "Microsoft FHIR Fanout Broker",
                    Version = "1.0.0"
                };

                // Configure REST capabilities
                var rest = capability.Rest?.FirstOrDefault() ?? new CapabilityStatement.RestComponent();
                rest.Mode = CapabilityStatement.RestfulCapabilityMode.Server;
                
                // Remove write operations - fanout broker is read-only
                rest.Interaction?.RemoveAll(i => 
                    i.Code == CapabilityStatement.SystemRestfulInteraction.Transaction ||
                    i.Code == CapabilityStatement.SystemRestfulInteraction.Batch);

                // Configure supported operations
                rest.Interaction = new List<CapabilityStatement.SystemInteractionComponent>
                {
                    new CapabilityStatement.SystemInteractionComponent
                    {
                        Code = CapabilityStatement.SystemRestfulInteraction.SearchSystem,
                        Documentation = "Search across all resource types on all configured FHIR servers"
                    }
                };

                if (capability.Rest == null)
                {
                    capability.Rest = new List<CapabilityStatement.RestComponent> { rest };
                }
            });
        }

        /// <inheritdoc />
        public async Task<CapabilityStatement> GetCapabilityStatementAsync(CancellationToken cancellationToken = default)
        {
            lock (_cacheLock)
            {
                if (_cachedCapabilityStatement != null && DateTime.UtcNow < _cacheExpiry)
                {
                    _logger.LogDebug("Returning cached capability statement");
                    return _cachedCapabilityStatement;
                }
            }

            try
            {
                _logger.LogInformation("Building capability statement from target FHIR servers");
                
                var capabilityStatement = await BuildCapabilityStatementAsync(cancellationToken);
                
                lock (_cacheLock)
                {
                    _cacheExpiry = DateTime.UtcNow.Add(_cacheLifetime);
                }

                return capabilityStatement;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building capability statement");
                
                // Return a basic capability statement in case of error
                return CreateBasicCapabilityStatement();
            }
        }

        private async Task<CapabilityStatement> BuildCapabilityStatementAsync(CancellationToken cancellationToken)
        {
            var enabledServers = _serverOrchestrator.GetEnabledServers();
            var serverCapabilities = new List<ServerCapabilityResult>();

            // Get capability statements from all enabled servers
            var capabilityTasks = enabledServers.Select(server => 
                _serverOrchestrator.GetCapabilityStatementAsync(server, cancellationToken));
            
            var results = await Task.WhenAll(capabilityTasks);
            serverCapabilities.AddRange(results.Where(r => r.IsSuccess));

            if (!serverCapabilities.Any())
            {
                _logger.LogWarning("No capability statements available from target servers");
                return CreateBasicCapabilityStatement();
            }

            // Intersect capabilities from all servers
            return IntersectCapabilities(serverCapabilities.Select(c => c.CapabilityStatement).ToList());
        }

        private CapabilityStatement IntersectCapabilities(List<CapabilityStatement> serverCapabilities)
        {
            var intersected = CreateBasicCapabilityStatement();
            
            if (!serverCapabilities.Any())
            {
                return intersected;
            }

            // Get the first server's REST component as baseline
            var firstServerRest = serverCapabilities.FirstOrDefault()?.Rest?.FirstOrDefault();
            if (firstServerRest?.Resource == null)
            {
                return intersected;
            }

            var intersectedRest = intersected.Rest.FirstOrDefault();
            if (intersectedRest == null)
            {
                intersectedRest = new CapabilityStatement.RestComponent { Mode = CapabilityStatement.RestfulCapabilityMode.Server };
                intersected.Rest.Add(intersectedRest);
            }

            // Intersect resource capabilities
            var resourceCapabilities = new List<CapabilityStatement.ResourceComponent>();
            
            foreach (var resourceType in firstServerRest.Resource.Select(r => r.Type).Distinct())
            {
                var resourcesOfType = serverCapabilities
                    .SelectMany(cap => cap.Rest?.FirstOrDefault()?.Resource ?? Enumerable.Empty<CapabilityStatement.ResourceComponent>())
                    .Where(r => r.Type == resourceType)
                    .ToList();

                if (resourcesOfType.Count == serverCapabilities.Count)
                {
                    // This resource type is supported by all servers
                    var intersectedResource = IntersectResourceCapabilities(resourcesOfType);
                    if (intersectedResource != null)
                    {
                        resourceCapabilities.Add(intersectedResource);
                    }
                }
            }

            intersectedRest.Resource = resourceCapabilities;

            _logger.LogInformation("Intersected capabilities from {ServerCount} servers, supporting {ResourceCount} resource types",
                serverCapabilities.Count, resourceCapabilities.Count);

            return intersected;
        }

        private CapabilityStatement.ResourceComponent IntersectResourceCapabilities(
            List<CapabilityStatement.ResourceComponent> resourceCapabilities)
        {
            if (!resourceCapabilities.Any())
            {
                return null;
            }

            var first = resourceCapabilities.First();
            var intersected = new CapabilityStatement.ResourceComponent
            {
                Type = first.Type,
                Profile = first.Profile, // Use first server's profile
                Interaction = new List<CapabilityStatement.ResourceInteractionComponent>(),
                SearchParam = new List<CapabilityStatement.SearchParamComponent>()
            };

            // Only include search-read interaction (fanout broker is read-only)
            intersected.Interaction.Add(new CapabilityStatement.ResourceInteractionComponent
            {
                Code = CapabilityStatement.TypeRestfulInteraction.SearchType,
                Documentation = $"Search {first.Type} resources across all configured FHIR servers"
            });

            // Intersect search parameters - only include parameters supported by all servers
            var commonSearchParams = resourceCapabilities
                .SelectMany(r => r.SearchParam ?? Enumerable.Empty<CapabilityStatement.SearchParamComponent>())
                .GroupBy(sp => sp.Name)
                .Where(g => g.Count() == resourceCapabilities.Count)
                .Select(g => g.First())
                .ToList();

            intersected.SearchParam = commonSearchParams;

            return intersected;
        }

        private CapabilityStatement CreateBasicCapabilityStatement()
        {
            return new CapabilityStatement
            {
                Id = "fanout-broker",
                Name = "FHIR Fanout Broker",
                Title = "FHIR Fanout Broker Query Service",
                Status = PublicationStatus.Active,
                Date = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssK"),
                Kind = CapabilityStatement.CapabilityStatementKind.Instance,
                Software = new CapabilityStatement.SoftwareComponent
                {
                    Name = "Microsoft FHIR Fanout Broker",
                    Version = "1.0.0"
                },
                Implementation = new CapabilityStatement.ImplementationComponent
                {
                    Description = "FHIR Fanout Broker Service for Multi-Server Search Aggregation"
                },
                FhirVersion = FHIRVersion.N4_0_1,
                Format = new List<string> { "json", "xml" },
                Rest = new List<CapabilityStatement.RestComponent>
                {
                    new CapabilityStatement.RestComponent
                    {
                        Mode = CapabilityStatement.RestfulCapabilityMode.Server,
                        Documentation = "Read-only FHIR service that aggregates search queries across multiple FHIR servers",
                        Security = new CapabilityStatement.SecurityComponent
                        {
                            Description = "Security is handled by individual target FHIR servers"
                        },
                        Interaction = new List<CapabilityStatement.SystemInteractionComponent>
                        {
                            new CapabilityStatement.SystemInteractionComponent
                            {
                                Code = CapabilityStatement.SystemRestfulInteraction.SearchSystem,
                                Documentation = "Search across all resource types on all configured FHIR servers"
                            }
                        }
                    }
                }
            };
        }
    }
}