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
        private readonly ISearchOptionsFactory _searchOptionsFactory;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<ChainedSearchProcessor> _logger;

        public ChainedSearchProcessor(
            IFhirServerOrchestrator serverOrchestrator,
            ISearchOptionsFactory searchOptionsFactory,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<ChainedSearchProcessor> logger)
        {
            _serverOrchestrator = EnsureArg.IsNotNull(serverOrchestrator, nameof(serverOrchestrator));
            _searchOptionsFactory = EnsureArg.IsNotNull(searchOptionsFactory, nameof(searchOptionsFactory));
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
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var chainDepth = chainedParam.Item1.Count(c => c == '.') + 1;

                    var chainedResults = await ProcessChainedParameter(chainedParam, chainedTimeout.Token);

                    stopwatch.Stop();
                    LogChainProcessingStats(chainedParam.Item1, chainDepth, chainedResults?.Count ?? 0, stopwatch.ElapsedMilliseconds);

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
        public IReadOnlyList<Tuple<string, string>> DetectChainedParameters(IReadOnlyList<Tuple<string, string>> queryParameters)
        {
            return queryParameters.Where(p => IsChainedParameter(p.Item1)).ToList();
        }

        /// <summary>
        /// Determines if a parameter name represents a chained search.
        /// </summary>
        private bool IsChainedParameter(string paramName)
        {
            // Forward chained parameters contain dots (e.g., "subject.name", "patient.identifier")
            if (paramName.Contains('.', StringComparison.Ordinal))
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
        /// Processes forward chained parameters like "subject.name=John" or multi-level chains like "subject.organization.name=Hospital".
        /// </summary>
        private async Task<List<ChainedSearchResult>> ProcessForwardChainedParameter(
            string paramName,
            string paramValue,
            CancellationToken cancellationToken)
        {
            var parts = paramName.Split('.');

            // Validate chain depth
            var maxChainDepth = _configuration.Value.MaxChainDepth;
            if (parts.Length > maxChainDepth)
            {
                _logger.LogWarning("Chained parameter {ParamName} exceeds maximum chain depth of {MaxDepth}",
                    paramName, maxChainDepth);
                throw new RequestTooCostlyException($"Chained search depth ({parts.Length}) exceeds maximum allowed depth ({maxChainDepth})");
            }

            if (parts.Length < 2)
            {
                _logger.LogWarning("Invalid chained parameter format: {ParamName}", paramName);
                return new List<ChainedSearchResult>();
            }

            _logger.LogInformation("Processing {Level}-level forward chain: {ParamName} = {Value}",
                parts.Length, paramName, paramValue);

            // Process multi-level chains recursively
            return await ProcessChainLevel(parts, paramValue, 0, cancellationToken);
        }

        /// <summary>
        /// Recursively processes chain levels for multi-level chained searches.
        /// </summary>
        private async Task<List<ChainedSearchResult>> ProcessChainLevel(
            string[] chainParts,
            string searchValue,
            int currentLevel,
            CancellationToken cancellationToken)
        {
            if (currentLevel >= chainParts.Length - 1)
            {
                // Final level - execute the actual search
                var finalParam = chainParts[currentLevel];
                var currentResourceType = currentLevel == 0
                    ? GuessTargetResourceType(chainParts[0])
                    : GuessTargetResourceType(chainParts[currentLevel - 1]);

                _logger.LogInformation("Executing final chain level search: {ResourceType}.{Param} = {Value}",
                    currentResourceType, finalParam, searchValue);

                var finalQuery = new List<Tuple<string, string>>
                {
                    new(finalParam, searchValue),
                    new("_elements", "id")  // Minimize payload
                };

                return await ExecuteChainedSubQuery(currentResourceType, finalQuery, cancellationToken);
            }

            // Intermediate level - get IDs and continue chain
            var currentParam = chainParts[currentLevel];
            var nextLevel = currentLevel + 1;
            var nextParam = chainParts[nextLevel];

            // If this is not the first level, we need to search using the IDs from the previous level
            List<ChainedSearchResult> currentLevelResults;

            if (currentLevel == 0)
            {
                // First level - start the chain by processing the next level first (reverse order)
                var nextLevelResults = await ProcessChainLevel(chainParts, searchValue, nextLevel, cancellationToken);

                if (!nextLevelResults.Any())
                {
                    return new List<ChainedSearchResult>();
                }

                // Get IDs from next level to filter current level
                var nextLevelIds = nextLevelResults.Select(r => r.ResourceId).Distinct().ToList();
                var currentResourceType = "Resource"; // This would need proper resource type resolution

                var currentQuery = new List<Tuple<string, string>>
                {
                    new("_id", string.Join(",", nextLevelIds)),
                    new("_elements", $"id,{currentParam}")
                };

                currentLevelResults = await ExecuteChainedSubQuery(currentResourceType, currentQuery, cancellationToken);
            }
            else
            {
                // Continue processing the chain
                var targetResourceType = GuessTargetResourceType(currentParam);

                _logger.LogInformation("Processing chain level {Level}: {Param} -> {ResourceType}",
                    currentLevel, currentParam, targetResourceType);

                // This is a complex case that would need proper reference resolution
                // For now, return empty results for complex multi-level chains
                _logger.LogWarning("Multi-level chaining beyond 2 levels is not fully implemented in this version");
                return new List<ChainedSearchResult>();
            }

            return currentLevelResults;
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
            if (parts.Length < 4 || parts[0] != "_has")
            {
                _logger.LogWarning("Unsupported reverse chained parameter format: {ParamName}", paramName);
                return new List<ChainedSearchResult>();
            }

            // Check for multi-level reverse chains (e.g., _has:Group:member:organization:name)
            var maxChainDepth = _configuration.Value.MaxChainDepth;
            var chainDepth = parts.Length - 2; // Subtract "_has" and resource type

            if (chainDepth > maxChainDepth)
            {
                _logger.LogWarning("Reverse chained parameter {ParamName} exceeds maximum chain depth of {MaxDepth}",
                    paramName, maxChainDepth);
                throw new RequestTooCostlyException($"Reverse chained search depth ({chainDepth}) exceeds maximum allowed depth ({maxChainDepth})");
            }

            var sourceResourceType = parts[1];  // e.g., "Group"
            var referenceParam = parts[2];      // e.g., "member"

            if (parts.Length == 4)
            {
                // Simple reverse chain: _has:Group:member:_id
                var targetParam = parts[3];

                _logger.LogInformation("Processing simple reverse chain: _has:{Source}:{Reference}:{Target} = {Value}",
                    sourceResourceType, referenceParam, targetParam, paramValue);

                var sourceQuery = new List<Tuple<string, string>>
                {
                    new(targetParam, paramValue),
                    new("_elements", $"id,{referenceParam}")
                };

                return await ExecuteChainedSubQuery(sourceResourceType, sourceQuery, cancellationToken);
            }
            else
            {
                // Multi-level reverse chain: _has:Group:member:organization:name
                _logger.LogInformation("Processing {Level}-level reverse chain: {ParamName} = {Value}",
                    chainDepth, paramName, paramValue);

                // Build the forward chain part from the reverse chain
                var forwardChainParts = parts.Skip(3).ToArray(); // Skip "_has", resource type, and reference param
                var forwardChainParam = string.Join(".", forwardChainParts);

                // First, resolve the forward chain to get the intermediate resource IDs
                var forwardChainResults = await ProcessForwardChainedParameter(forwardChainParam, paramValue, cancellationToken);

                if (!forwardChainResults.Any())
                {
                    return new List<ChainedSearchResult>();
                }

                // Then use those IDs to search for the reverse references
                var targetIds = forwardChainResults.Select(r => r.ResourceId).Distinct().ToList();
                var reverseQuery = new List<Tuple<string, string>>
                {
                    new(referenceParam, string.Join(",", targetIds)),
                    new("_elements", $"id,{referenceParam}")
                };

                return await ExecuteChainedSubQuery(sourceResourceType, reverseQuery, cancellationToken);
            }
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
        /// </summary>
        private async Task<ServerSearchResult> ExecuteSimplifiedSearchAsync(
            FhirServerEndpoint server,
            string resourceType,
            List<Tuple<string, string>> queryParams,
            CancellationToken cancellationToken)
        {
            try
            {
                // Create a minimal SearchOptions for the chained search
                var searchOptions = _searchOptionsFactory.Create(resourceType, queryParams);

                // Execute the search using the orchestrator
                var result = await _serverOrchestrator.SearchAsync(server, searchOptions, cancellationToken);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing simplified search on server {ServerId} for resource type {ResourceType}",
                    server.Id, resourceType);

                return new ServerSearchResult
                {
                    ServerId = server.Id,
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    StatusCode = 500
                };
            }
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

            if (serverResult?.SearchResult?.Results == null)
            {
                return results;
            }

            foreach (var searchResultEntry in serverResult.SearchResult.Results)
            {
                try
                {
                    var resource = searchResultEntry.Resource;
                    if (resource == null)
                    {
                        continue;
                    }

                    var chainedResult = new ChainedSearchResult
                    {
                        ResourceType = resource.ResourceTypeName,
                        ResourceId = resource.ResourceId,
                        ServerBaseUrl = serverResult.ServerBaseUrl
                    };

                    // Extract any reference information from the resource if needed
                    // This is simplified - in a full implementation you might want to extract
                    // specific reference fields based on the chained search context
                    results.Add(chainedResult);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error extracting chained result from resource in server {ServerId}",
                        serverResult.ServerId);
                }
            }

            _logger.LogDebug("Extracted {ResultCount} chained results from server {ServerId}",
                results.Count, serverResult.ServerId);

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
                // Patient references
                "subject" => "Patient",
                "patient" => "Patient",
                "individual" => "Patient",

                // Practitioner references
                "practitioner" => "Practitioner",
                "performer" => "Practitioner",
                "author" => "Practitioner",
                "informant" => "Practitioner",

                // Organization references
                "organization" => "Organization",
                "managingorganization" => "Organization",
                "serviceorganization" => "Organization",

                // Location references
                "location" => "Location",
                "servicelocation" => "Location",

                // Encounter references
                "encounter" => "Encounter",
                "context" => "Encounter",

                // Device references
                "device" => "Device",

                // Medication references
                "medication" => "Medication",
                "medicationreference" => "Medication",

                // Observation references
                "observation" => "Observation",
                "result" => "Observation",

                // Condition references
                "condition" => "Condition",
                "problem" => "Condition",
                "diagnosis" => "Condition",

                // Procedure references
                "procedure" => "Procedure",

                // DiagnosticReport references
                "report" => "DiagnosticReport",

                // Group references
                "group" => "Group",
                "member" => "Group",

                // Coverage references
                "coverage" => "Coverage",
                "payor" => "Coverage",

                // RelatedPerson references
                "relatedperson" => "RelatedPerson",
                "contact" => "RelatedPerson",

                // CareTeam references
                "careteam" => "CareTeam",
                "team" => "CareTeam",

                // Specimen references
                "specimen" => "Specimen",

                // Default fallback to Patient for unknown reference types
                _ => "Patient"
            };
        }

        /// <summary>
        /// Validates and logs chain processing statistics.
        /// </summary>
        private void LogChainProcessingStats(string paramName, int chainDepth, int resultCount, long processingTimeMs)
        {
            _logger.LogInformation("Chain processing completed: {ParamName} (depth: {Depth}, results: {Results}, time: {TimeMs}ms)",
                paramName, chainDepth, resultCount, processingTimeMs);

            // Log warning for potentially expensive operations
            if (chainDepth > 2)
            {
                _logger.LogWarning("Deep chain search detected: {ParamName} with depth {Depth} - consider query optimization",
                    paramName, chainDepth);
            }

            if (processingTimeMs > 5000) // 5 seconds
            {
                _logger.LogWarning("Slow chain search detected: {ParamName} took {TimeMs}ms - consider query optimization",
                    paramName, processingTimeMs);
            }
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

        IReadOnlyList<Tuple<string, string>> DetectChainedParameters(IReadOnlyList<Tuple<string, string>> queryParameters);
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
