// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Routing;
using Microsoft.Health.Fhir.Core.Features.Search;

namespace Microsoft.Health.Fhir.FanoutBroker.Controllers
{
    /// <summary>
    /// Controller for FHIR fanout broker operations.
    /// Handles read-only search operations and rejects write operations.
    /// </summary>
    [ApiController]
    [Route("")]
    [SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Justification = "ASP.NET Controller")]
    public class FanoutController : ControllerBase
    {
        private readonly ISearchService _searchService;
        private readonly IConformanceProvider _conformanceProvider;
        private readonly IResourceDeserializer _resourceDeserializer;
        private readonly ILogger<FanoutController> _logger;

        public FanoutController(
            ISearchService searchService,
            IConformanceProvider conformanceProvider,
            IResourceDeserializer resourceDeserializer,
            ILogger<FanoutController> logger)
        {
            _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
            _conformanceProvider = conformanceProvider ?? throw new ArgumentNullException(nameof(conformanceProvider));
            _resourceDeserializer = resourceDeserializer ?? throw new ArgumentNullException(nameof(resourceDeserializer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Handle system-level search operations (GET /?[search]).
        /// </summary>
        [HttpGet("")]
        public async Task<IActionResult> SystemSearch(CancellationToken cancellationToken)
        {
            try
            {
                var queryParameters = GetQueryParameters();

                _logger.LogInformation("System-level search request with {ParamCount} parameters", queryParameters.Count);

                var result = await _searchService.SearchAsync(
                    resourceType: null,
                    queryParameters: queryParameters,
                    cancellationToken: cancellationToken);

                return Ok(CreateBundle(result));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during system-level search");
                return StatusCode(500, CreateOperationOutcome("Error during system-level search", ex.Message));
            }
        }

        /// <summary>
        /// Handle resource type-level search operations (GET /[ResourceType]?[search]).
        /// </summary>
        [HttpGet("{resourceType}")]
        public async Task<IActionResult> ResourceSearch(string resourceType, CancellationToken cancellationToken)
        {
            try
            {
                var queryParameters = GetQueryParameters();

                _logger.LogInformation("Resource search request for {ResourceType} with {ParamCount} parameters", resourceType, queryParameters.Count);

                var result = await _searchService.SearchAsync(
                    resourceType: resourceType,
                    queryParameters: queryParameters,
                    cancellationToken: cancellationToken);

                return Ok(CreateBundle(result));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during resource search for {ResourceType}", resourceType);
                return StatusCode(500, CreateOperationOutcome("Error during resource search", ex.Message));
            }
        }

        /// <summary>
        /// Handle $includes operation for paginated retrieval of included resources.
        /// </summary>
        [HttpGet("{resourceType}/$includes")]
        public async Task<IActionResult> SearchIncludes(string resourceType, CancellationToken cancellationToken)
        {
            try
            {
                var queryParameters = GetQueryParameters();

                _logger.LogInformation("$includes operation request for {ResourceType} with {ParamCount} parameters", resourceType, queryParameters.Count);

                var result = await _searchService.SearchAsync(
                    resourceType: resourceType,
                    queryParameters: queryParameters,
                    cancellationToken: cancellationToken,
                    isIncludesOperation: true);

                return Ok(CreateBundle(result));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during $includes operation for {ResourceType}", resourceType);
                return StatusCode(500, CreateOperationOutcome("Error during $includes operation", ex.Message));
            }
        }

        /// <summary>
        /// Handle capability statement requests.
        /// </summary>
        [HttpGet("metadata")]
        public async Task<IActionResult> GetCapabilityStatement(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Capability statement request");

                var capabilityStatement = await _conformanceProvider.GetMetadata(cancellationToken);
                return Ok(capabilityStatement);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting capability statement");
                return StatusCode(500, CreateOperationOutcome("Error getting capability statement", ex.Message));
            }
        }

        /// <summary>
        /// Reject point read operations (GET /[ResourceType]/[id]).
        /// </summary>
        [HttpGet("{resourceType}/{id}")]
        public IActionResult RejectPointRead(string resourceType, string id)
        {
            _logger.LogWarning("Rejected point read request for {ResourceType}/{Id}", resourceType, id);

            return StatusCode(501, CreateOperationOutcome(
                "Not Implemented",
                "Point read operations are not supported by the fanout broker service. Use search operations instead."));
        }

        /// <summary>
        /// Reject versioned read operations (GET /[ResourceType]/[id]/_history/[vid]).
        /// </summary>
        [HttpGet("{resourceType}/{id}/_history/{vid}")]
        public IActionResult RejectVersionedRead(string resourceType, string id, string vid)
        {
            _logger.LogWarning("Rejected versioned read request for {ResourceType}/{Id}/_history/{Vid}", resourceType, id, vid);

            return StatusCode(501, CreateOperationOutcome(
                "Not Implemented",
                "Versioned read operations are not supported by the fanout broker service."));
        }

        /// <summary>
        /// Reject all Create operations (POST).
        /// </summary>
        [HttpPost]
        [HttpPost("{resourceType}")]
        public IActionResult RejectCreate()
        {
            _logger.LogWarning("Rejected CREATE request");

            return StatusCode(405, CreateOperationOutcome(
                "Method Not Allowed",
                "Create operations are not supported by the fanout broker service. This is a read-only service for search aggregation."));
        }

        /// <summary>
        /// Reject all Update operations (PUT).
        /// </summary>
        [HttpPut]
        [HttpPut("{resourceType}")]
        [HttpPut("{resourceType}/{id}")]
        public IActionResult RejectUpdate()
        {
            _logger.LogWarning("Rejected UPDATE request");

            return StatusCode(405, CreateOperationOutcome(
                "Method Not Allowed",
                "Update operations are not supported by the fanout broker service. This is a read-only service for search aggregation."));
        }

        /// <summary>
        /// Reject all Delete operations (DELETE).
        /// </summary>
        [HttpDelete]
        [HttpDelete("{resourceType}")]
        [HttpDelete("{resourceType}/{id}")]
        public IActionResult RejectDelete()
        {
            _logger.LogWarning("Rejected DELETE request");

            return StatusCode(405, CreateOperationOutcome(
                "Method Not Allowed",
                "Delete operations are not supported by the fanout broker service. This is a read-only service for search aggregation."));
        }

        /// <summary>
        /// Reject all Patch operations (PATCH).
        /// </summary>
        [HttpPatch]
        [HttpPatch("{resourceType}")]
        [HttpPatch("{resourceType}/{id}")]
        public IActionResult RejectPatch()
        {
            _logger.LogWarning("Rejected PATCH request");

            return StatusCode(405, CreateOperationOutcome(
                "Method Not Allowed",
                "Patch operations are not supported by the fanout broker service. This is a read-only service for search aggregation."));
        }

        private List<Tuple<string, string>> GetQueryParameters()
        {
            var queryParams = new List<Tuple<string, string>>();

            foreach (var param in Request.Query)
            {
                foreach (var value in param.Value)
                {
                    queryParams.Add(new Tuple<string, string>(param.Key, value));
                }
            }

            return queryParams;
        }

        private Bundle CreateBundle(SearchResult searchResult)
        {
            var bundle = new Bundle
            {
                Type = Bundle.BundleType.Searchset,
                Entry = new List<Bundle.EntryComponent>(),
            };

            if (searchResult.Results != null)
            {
                foreach (var result in searchResult.Results)
                {
                    // Generate the full URL using the URL resolver with fanout context
                    string fullUrl;
                    if (!string.IsNullOrEmpty(searchResult.SourceServer))
                    {
                        // For fanout scenarios, generate URL with source server context for traceability
                        // Pattern: {source-server-base-url}/{resourceType}/{id}
                        var sourceServerBaseUrl = searchResult.SourceServer.TrimEnd('/');
                        fullUrl = $"{sourceServerBaseUrl}/{result.Resource.ResourceTypeName}/{result.Resource.ResourceId}";

                        _logger.LogDebug("Generated fanout URL with source server context: {FullUrl}", fullUrl);
                    }
                    else
                    {
                        // Standard URL resolution for non-fanout scenarios
                        throw new NotImplementedException();
                    }

                    // Deserialize ResourceWrapper to ResourceElement, then convert to Resource
                    var resourceElement = _resourceDeserializer.Deserialize(result.Resource);
                    var fhirResource = resourceElement.ToPoco();

                    var entry = new Bundle.EntryComponent
                    {
                        Resource = fhirResource,
                        FullUrl = fullUrl,
                        Search = new Bundle.SearchComponent
                        {
                            Mode = Bundle.SearchEntryMode.Match,
                        },
                    };

                    bundle.Entry.Add(entry);
                }
            }

            // Add total count if available
            if (searchResult.TotalCount.HasValue)
            {
                bundle.Total = searchResult.TotalCount.Value;
            }

            // Add continuation token for paging
            if (!string.IsNullOrEmpty(searchResult.ContinuationToken))
            {
                var nextLink = new Bundle.LinkComponent
                {
                    Relation = "next",
                    Url = Request.GetDisplayUrl() + $"&ct={searchResult.ContinuationToken}",
                };
                bundle.Link = new List<Bundle.LinkComponent> { nextLink };
            }

            return bundle;
        }

        private static OperationOutcome CreateOperationOutcome(string severity, string details)
        {
            return new OperationOutcome
            {
                Issue = new List<OperationOutcome.IssueComponent>
                {
                    new OperationOutcome.IssueComponent
                    {
                        Severity = OperationOutcome.IssueSeverity.Error,
                        Code = OperationOutcome.IssueType.NotSupported,
                        Diagnostics = details,
                    },
                },
            };
        }
    }
}
