// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Processes chained search expressions across multiple FHIR servers with timeout protection.
    /// This implementation works at the HTTP/query level rather than integrating deeply with the FHIR search engine.
    /// </summary>
    public class ChainedSearchProcessor : IChainedSearchProcessor
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<ChainedSearchProcessor> _logger;

        public ChainedSearchProcessor(
            IFhirServerOrchestrator serverOrchestrator,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<ChainedSearchProcessor> logger)
        {
            _serverOrchestrator = EnsureArg.IsNotNull(serverOrchestrator, nameof(serverOrchestrator));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <summary>
        /// Processes chained search queries by detecting chained parameters and executing distributed sub-queries.
        /// This implementation uses HTTP-level query processing similar to FhirCosmosSearchService but distributed.
        /// </summary>
        /// <param name="resourceType">The target resource type for the search.</param>
        /// <param name="queryParameters">The query parameters including chained expressions.</param>
        /// <param name="cancellationToken">Cancellation token with timeout protection.</param>
        /// <returns>Modified query parameters that replace chained expressions with ID filters.</returns>
        public async Task<List<Tuple<string, string>>> ProcessChainedSearchAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resourceType, nameof(resourceType));
            EnsureArg.IsNotNull(queryParameters, nameof(queryParameters));

            // Detect chained search parameters (containing dots like "subject.name" or reverse chains like "_has:Group:member:_id")
            var chainedParams = DetectChainedParameters(queryParameters);
            
            if (!chainedParams.Any())
            {
                // No chained parameters - return original query
                return queryParameters.ToList();
            }

            _logger.LogInformation("Processing {ChainCount} chained search parameters for resource type {ResourceType}", 
                chainedParams.Count, resourceType);

            var modifiedParams = queryParameters.Where(p => !IsChainedParameter(p.Item1)).ToList();
            
            try
            {
                // Create timeout for chained search operations
                using var chainedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                chainedTimeout.CancelAfter(TimeSpan.FromSeconds(_configuration.Value.ChainSearchTimeoutSeconds));

                // Process each chained parameter
                foreach (var chainedParam in chainedParams)
                {
                    var chainedResults = await ProcessChainedParameter(chainedParam, chainedTimeout.Token);
                    
                    if (chainedResults?.Any() == true)
                    {
                        // Convert chained results into ID filters
                        var idFilter = ConvertToIdFilter(chainedParam, chainedResults);
                        if (idFilter != null)
                        {
                            modifiedParams.Add(idFilter);
                        }
                    }
                    else
                    {
                        // No results from chained search - query should return empty result set
                        _logger.LogInformation("Chained parameter {Parameter} returned no results - adding impossible filter", chainedParam.Item1);
                        modifiedParams.Add(new Tuple<string, string>("_id", "impossible-id-no-results"));
                        break; // No point processing other chains if one returns empty
                    }
                }
                
                return modifiedParams;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Chained search timed out after {Timeout} seconds", 
                    _configuration.Value.ChainSearchTimeoutSeconds);
                throw new RequestTooCostlyException("Chained search operation timed out - query too complex");
            }
        }

        /// <summary>
        /// Detects parameters that represent chained searches.
        /// Examples: "subject.name", "patient.identifier", "_has:Group:member:_id"
        /// </summary>
        private List<Tuple<string, string>> DetectChainedParameters(IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            return queryParameters.Where(p => IsChainedParameter(p.Item1)).ToList();
        }

        /// <summary>
        /// Determines if a parameter name represents a chained search.
        /// </summary>
        private bool IsChainedParameter(string paramName)
        {
            // Forward chained parameters contain dots (e.g., "subject.name", "patient.identifier")
            if (paramName.Contains('.'))
            {
                return true;
            }

            // Reverse chained parameters start with "_has:" (e.g., "_has:Group:member:_id")
            if (paramName.StartsWith("_has:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Processes a single chained parameter by executing sub-queries across all servers.
        /// </summary>
        private async Task<List<ChainedSearchResult>> ProcessChainedParameter(
            Tuple<string, string> chainedParam,
            CancellationToken cancellationToken)
        {
            var (paramName, paramValue) = chainedParam;
            
            if (paramName.StartsWith("_has:", StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessReverseChainedParameter(paramName, paramValue, cancellationToken);
            }
            else
            {
                return await ProcessForwardChainedParameter(paramName, paramValue, cancellationToken);
            }
        }

        /// <summary>
        /// Processes forward chained parameters like "subject.name=John".
        /// </summary>
        private async Task<List<ChainedSearchResult>> ProcessForwardChainedParameter(
            string paramName, 
            string paramValue, 
            CancellationToken cancellationToken)
        {
            var parts = paramName.Split('.');
            if (parts.Length != 2)
            {
                _logger.LogWarning("Unsupported chained parameter format: {ParamName}", paramName);
                return new List<ChainedSearchResult>();
            }

            var referenceParam = parts[0];      // e.g., "subject"
            var targetParam = parts[1];         // e.g., "name"
            
            // Determine target resource type (simplified - in real implementation this would use search parameter definitions)
            var targetResourceType = GuessTargetResourceType(referenceParam);
            
            _logger.LogInformation("Processing forward chain: {Reference}.{Target} = {Value} -> searching {ResourceType}", 
                referenceParam, targetParam, paramValue, targetResourceType);

            // Execute search on target resource type with optimized projection
            var targetQuery = new List<Tuple<string, string>>
            {
                new(targetParam, paramValue),
                new("_elements", "id")  // Minimize payload as suggested in the comment
            };

            return await ExecuteChainedSubQuery(targetResourceType, targetQuery, cancellationToken);
        }

        /// <summary>
        /// Processes reverse chained parameters like "_has:Group:member:_id=group1".
        /// </summary>
        private async Task<List<ChainedSearchResult>> ProcessReverseChainedParameter(
            string paramName, 
            string paramValue, 
            CancellationToken cancellationToken)
        {
            // Parse _has:ResourceType:SearchParam:TargetParam format
            var parts = paramName.Split(':');
            if (parts.Length != 4 || parts[0] != "_has")
            {
                _logger.LogWarning("Unsupported reverse chained parameter format: {ParamName}", paramName);
                return new List<ChainedSearchResult>();
            }

            var sourceResourceType = parts[1];  // e.g., "Group"
            var referenceParam = parts[2];      // e.g., "member"
            var targetParam = parts[3];         // e.g., "_id"
            
            _logger.LogInformation("Processing reverse chain: _has:{Source}:{Reference}:{Target} = {Value}", 
                sourceResourceType, referenceParam, targetParam, paramValue);

            // Execute search on source resource type
            var sourceQuery = new List<Tuple<string, string>>
            {
                new(targetParam, paramValue),
                new("_elements", $"id,{referenceParam}")  // Get ID and reference information
            };

            return await ExecuteChainedSubQuery(sourceResourceType, sourceQuery, cancellationToken);
        }

        /// <summary>
        /// Executes a sub-query across all servers for chained search processing.
        /// </summary>
        private async Task<List<ChainedSearchResult>> ExecuteChainedSubQuery(
            string resourceType,
            List<Tuple<string, string>> queryParams,
            CancellationToken cancellationToken)
        {
            var results = new List<ChainedSearchResult>();
            var enabledServers = _serverOrchestrator.GetEnabledServers();

            _logger.LogInformation("Executing chained sub-query for {ResourceType} across {ServerCount} servers", 
                resourceType, enabledServers.Count);

            // Create search tasks for all servers
            var searchTasks = enabledServers.Select(async server =>
            {
                try
                {
                    // Build query string
                    var queryString = string.Join("&", queryParams.Select(p => $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2)}"));
                    var searchUrl = $"{resourceType}?{queryString}";

                    // Use a simplified search approach since we're working as a proxy
                    // This would need to be implemented in the FhirServerOrchestrator
                    var serverResult = await ExecuteSimplifiedSearchAsync(server, resourceType, queryParams, cancellationToken);
                    
                    if (serverResult?.SearchResult?.Results != null)
                    {
                        return ExtractChainedResults(serverResult);
                    }
                    
                    return Enumerable.Empty<ChainedSearchResult>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing chained sub-query on server {ServerId}", server.Id);
                    return Enumerable.Empty<ChainedSearchResult>();
                }
            });

            var allResults = await Task.WhenAll(searchTasks);
            results.AddRange(allResults.SelectMany(r => r));

            // Deduplicate results
            var uniqueResults = results.GroupBy(r => $"{r.ResourceType}|{r.ResourceId}").Select(g => g.First()).ToList();

            _logger.LogInformation("Chained sub-query returned {ResultCount} unique results", uniqueResults.Count);

            return uniqueResults;
        }

        /// <summary>
        /// Executes a simplified search that works with query parameters directly.
        /// This is a placeholder for the actual implementation.
        /// </summary>
        private async Task<ServerSearchResult> ExecuteSimplifiedSearchAsync(
            FhirServerEndpoint server,
            string resourceType,
            List<Tuple<string, string>> queryParams,
            CancellationToken cancellationToken)
        {
            // This would need to be implemented in the FhirServerOrchestrator
            // For now, return a placeholder
            _logger.LogWarning("ExecuteSimplifiedSearchAsync not yet implemented - chained search will not work correctly");
            
            await Task.Delay(100, cancellationToken); // Simulate async operation
            
            return new ServerSearchResult
            {
                ServerId = server.Id,
                IsSuccess = false,
                ErrorMessage = "Simplified search not implemented"
            };
        }

        /// <summary>
        /// Converts chained search results into ID filter parameters.
        /// </summary>
        private Tuple<string, string> ConvertToIdFilter(Tuple<string, string> originalParam, List<ChainedSearchResult> results)
        {
            if (!results.Any())
            {
                return null;
            }

            var resourceIds = results.Select(r => r.ResourceId).Distinct();
            var idFilter = string.Join(",", resourceIds);
            
            // For forward chains, we filter by the reference parameter
            // For reverse chains, we filter by _id
            if (originalParam.Item1.StartsWith("_has:", StringComparison.OrdinalIgnoreCase))
            {
                return new Tuple<string, string>("_id", idFilter);
            }
            else
            {
                var referenceParam = originalParam.Item1.Split('.')[0];
                return new Tuple<string, string>(referenceParam, idFilter);
            }
        }

        /// <summary>
        /// Extracts chained search results from a server search response.
        /// </summary>
        private IEnumerable<ChainedSearchResult> ExtractChainedResults(ServerSearchResult serverResult)
        {
            var results = new List<ChainedSearchResult>();

            // This would extract resource IDs and any reference information from the search results
            // For now, return empty since we need the full SearchResult structure
            _logger.LogWarning("ExtractChainedResults not fully implemented - requires SearchResult parsing");

            return results;
        }

        /// <summary>
        /// Attempts to guess the target resource type from a reference parameter name.
        /// This is a simplified implementation - real implementation would use search parameter definitions.
        /// </summary>
        private string GuessTargetResourceType(string referenceParam)
        {
            return referenceParam.ToLowerInvariant() switch
            {
                "subject" => "Patient",
                "patient" => "Patient",
                "practitioner" => "Practitioner",
                "organization" => "Organization",
                "location" => "Location",
                "encounter" => "Encounter",
                _ => "Patient" // Default fallback
            };
        }
    }

    /// <summary>
    /// Updated interface for chained search processing at the HTTP query level.
    /// </summary>
    public interface IChainedSearchProcessor
    {
        Task<List<Tuple<string, string>>> ProcessChainedSearchAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Result from a chained search operation.
    /// </summary>
    public class ChainedSearchResult
    {
        public string ResourceType { get; set; }
        public string ResourceId { get; set; }
        public string ServerBaseUrl { get; set; }
        public Dictionary<string, object> References { get; set; } = new();
    }
}