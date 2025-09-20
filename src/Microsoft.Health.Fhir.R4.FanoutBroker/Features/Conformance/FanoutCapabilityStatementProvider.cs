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
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Models;
using Task = System.Threading.Tasks.Task;

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
        private ResourceElement _cachedCapabilityStatement;
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
            _cachedCapabilityStatement = null;
        }

        /// <inheritdoc />
        public void Build(ICapabilityStatementBuilder builder)
        {
            // Configure basic read-only search capabilities using the shared builder API.
            // Avoid customizing HL7 model directly; rely on core builder to populate defaults and sync search params/profiles.
            builder
                .AddGlobalSearchParameters()
                .SyncSearchParametersAsync()
                .SyncProfiles();
        }

        /// <inheritdoc />
        public async Task<ResourceElement> GetCapabilityStatementOnStartup(CancellationToken cancellationToken = default)
        {
            var capabilityStatement = await GetCapabilityStatementAsync(cancellationToken);
            // Convert FHIR model to ResourceElement (this would need proper implementation)
            // For now, return null as this is a complex conversion
            return null;
        }

        /// <inheritdoc />
        public async Task<ResourceElement> GetMetadata(CancellationToken cancellationToken = default)
        {
            var capabilityStatement = await GetCapabilityStatementAsync(cancellationToken);
            // Convert FHIR model to ResourceElement (this would need proper implementation)
            // For now, return null as this is a complex conversion
            return null;
        }

        /// <inheritdoc />
        public Task<bool> SatisfiesAsync(IReadOnlyCollection<CapabilityQuery> queries, CancellationToken cancellationToken = default)
        {
            // For now, assume all queries are satisfied (this would need proper implementation)
            return Task.FromResult(true);
        }

        /// <summary>
        /// Gets the capability statement as a FHIR CapabilityStatement resource.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The capability statement.</returns>
        public async Task<CapabilityStatement> GetCapabilityStatementAsync(CancellationToken cancellationToken = default)
        {
            lock (_cacheLock)
            {
                if (_cachedCapabilityStatement != null && DateTime.UtcNow < _cacheExpiry)
                {
                    _logger.LogDebug("Returning cached capability statement");
                    return ConvertResourceElementToCapabilityStatement(_cachedCapabilityStatement);
                }
            }

            try
            {
                _logger.LogInformation("Building capability statement from target FHIR servers");

                var capabilityStatement = await BuildCapabilityStatementAsync(cancellationToken);

                lock (_cacheLock)
                {
                    // Cache would be set here in a real implementation
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

        private CapabilityStatement ConvertResourceElementToCapabilityStatement(ResourceElement resourceElement)
        {
            // This would need proper implementation to convert ResourceElement to CapabilityStatement
            // For now, return a basic capability statement
            return CreateBasicCapabilityStatement();
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

            // Get all unique resource types across all servers
            var allResourceTypes = serverCapabilities
                .SelectMany(cap => cap.Rest?.FirstOrDefault()?.Resource ?? Enumerable.Empty<CapabilityStatement.ResourceComponent>())
                .Select(r => r.Type)
                .Distinct()
                .ToList();

            var intersectedRest = intersected.Rest.FirstOrDefault();
            if (intersectedRest == null)
            {
                intersectedRest = new CapabilityStatement.RestComponent { Mode = CapabilityStatement.RestfulCapabilityMode.Server };
                intersected.Rest.Add(intersectedRest);
            }

            // Intersect resource capabilities
            var resourceCapabilities = new List<CapabilityStatement.ResourceComponent>();
            var serverCount = serverCapabilities.Count;
            var supportMatrix = new Dictionary<string, int>();

            foreach (var resourceType in allResourceTypes)
            {
                var resourcesOfType = serverCapabilities
                    .SelectMany(cap => cap.Rest?.FirstOrDefault()?.Resource ?? Enumerable.Empty<CapabilityStatement.ResourceComponent>())
                    .Where(r => r.Type == resourceType)
                    .ToList();

                supportMatrix[resourceType.ToString()] = resourcesOfType.Count;

                // Include resource types supported by at least 50% of servers (configurable threshold)
                var minimumSupportThreshold = Math.Max(1, (int)Math.Ceiling(serverCount * 0.5));

                if (resourcesOfType.Count >= minimumSupportThreshold)
                {
                    var intersectedResource = IntersectResourceCapabilities(resourcesOfType, resourcesOfType.Count == serverCount);
                    if (intersectedResource != null)
                    {
                        resourceCapabilities.Add(intersectedResource);
                    }
                }
            }

            intersectedRest.Resource = resourceCapabilities;

            // Intersect system-level search parameters
            IntersectSystemLevelCapabilities(serverCapabilities, intersectedRest);

            // Add metadata about server support matrix
            var documentation = $"Fanout broker aggregating {serverCount} FHIR servers. " +
                               $"Resource support: {string.Join(", ", supportMatrix.Select(kv => $"{kv.Key}({kv.Value}/{serverCount})"))}";
            intersectedRest.Documentation = documentation;

            _logger.LogInformation("Intersected capabilities from {ServerCount} servers, supporting {ResourceCount} resource types. Support matrix: {SupportMatrix}",
                serverCount, resourceCapabilities.Count, string.Join(", ", supportMatrix.Select(kv => $"{kv.Key}:{kv.Value}")));

            return intersected;
        }

        private CapabilityStatement.ResourceComponent IntersectResourceCapabilities(
            List<CapabilityStatement.ResourceComponent> resourceCapabilities,
            bool isUniversallySupported)
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
                SearchParam = new List<CapabilityStatement.SearchParamComponent>(),
                SearchInclude = new List<string>(),
                SearchRevInclude = new List<string>()
            };

            // Add fanout-specific interactions
            intersected.Interaction.Add(new CapabilityStatement.ResourceInteractionComponent
            {
                Code = CapabilityStatement.TypeRestfulInteraction.SearchType,
                Documentation = isUniversallySupported
                    ? $"Search {first.Type} resources across all configured FHIR servers"
                    : $"Search {first.Type} resources across servers that support this resource type"
            });

            // Add support for include operations
            intersected.Interaction.Add(new CapabilityStatement.ResourceInteractionComponent
            {
                Code = CapabilityStatement.TypeRestfulInteraction.Read,
                Documentation = $"Read {first.Type} resources via $includes operation when included in search results"
            });

            // Intersect search parameters
            var searchParamThreshold = isUniversallySupported
                ? resourceCapabilities.Count  // All servers must support for universal resources
                : Math.Max(1, (int)Math.Ceiling(resourceCapabilities.Count * 0.7)); // 70% for partial support

            var commonSearchParams = resourceCapabilities
                .SelectMany(r => r.SearchParam ?? Enumerable.Empty<CapabilityStatement.SearchParamComponent>())
                .GroupBy(sp => sp.Name)
                .Where(g => g.Count() >= searchParamThreshold)
                .Select(g => g.First())
                .ToList();

            intersected.SearchParam = commonSearchParams;

            // Intersect _include and _revinclude capabilities
            var commonIncludes = resourceCapabilities
                .SelectMany(r => r.SearchInclude ?? Enumerable.Empty<string>())
                .GroupBy(inc => inc)
                .Where(g => g.Count() >= searchParamThreshold)
                .Select(g => g.Key)
                .ToList();

            var commonRevIncludes = resourceCapabilities
                .SelectMany(r => r.SearchRevInclude ?? Enumerable.Empty<string>())
                .GroupBy(inc => inc)
                .Where(g => g.Count() >= searchParamThreshold)
                .Select(g => g.Key)
                .ToList();

            intersected.SearchInclude = commonIncludes;
            intersected.SearchRevInclude = commonRevIncludes;

            // Add versioning information if available
            if (resourceCapabilities.Any(r => r.Versioning != null))
            {
                var versioningSupport = resourceCapabilities
                    .Select(r => r.Versioning)
                    .Where(v => v != null)
                    .GroupBy(v => v)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key;

                intersected.Versioning = versioningSupport;
            }

            return intersected;
        }

        private void IntersectSystemLevelCapabilities(
            List<CapabilityStatement> serverCapabilities,
            CapabilityStatement.RestComponent intersectedRest)
        {
            // Add common global search parameters
            var globalSearchParams = new List<CapabilityStatement.SearchParamComponent>
            {
                new CapabilityStatement.SearchParamComponent
                {
                    Name = "_id",
                    Type = SearchParamType.Token,
                    Documentation = "Logical resource identifier"
                },
                new CapabilityStatement.SearchParamComponent
                {
                    Name = "_lastUpdated",
                    Type = SearchParamType.Date,
                    Documentation = "Last modified date"
                },
                new CapabilityStatement.SearchParamComponent
                {
                    Name = "_count",
                    Type = SearchParamType.Number,
                    Documentation = "Number of resources to return in a page"
                },
                new CapabilityStatement.SearchParamComponent
                {
                    Name = "_include",
                    Type = SearchParamType.String,
                    Documentation = "Include referenced resources"
                },
                new CapabilityStatement.SearchParamComponent
                {
                    Name = "_revinclude",
                    Type = SearchParamType.String,
                    Documentation = "Include resources that reference this resource"
                }
            };

            intersectedRest.SearchParam = globalSearchParams;

            // Add fanout-specific operations
            var operations = new List<CapabilityStatement.OperationComponent>
            {
                new CapabilityStatement.OperationComponent
                {
                    Name = "$includes",
                    Definition = "http://hl7.org/fhir/OperationDefinition/Resource-includes",
                    Documentation = "Retrieve included resources separately to handle large include sets"
                }
            };

            intersectedRest.Operation = operations;

            // Set security information
            if (intersectedRest.Security == null)
            {
                intersectedRest.Security = new CapabilityStatement.SecurityComponent
                {
                    Description = "Security is delegated to individual target FHIR servers. " +
                                 "Authentication and authorization are handled by the fanout broker " +
                                 "when configured with server-specific credentials."
                };
            }
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
                Kind = CapabilityStatementKind.Instance,
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
