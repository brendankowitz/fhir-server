// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Extensions;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Routing;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Messages.Search;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Resources
{
    public class ResourceReferenceResolver
    {
        private readonly ISearchService _searchService;
        private readonly IQueryStringParser _queryStringParser;
        private readonly ILogger<ResourceReferenceResolver> _logger;

        public ResourceReferenceResolver(
            ISearchService searchService,
            IQueryStringParser queryStringParser,
            ILogger<ResourceReferenceResolver> logger)
        {
            _searchService = EnsureArg.IsNotNull(searchService, nameof(searchService));
            _queryStringParser = EnsureArg.IsNotNull(queryStringParser, nameof(queryStringParser));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        public async Task<int> ResolveReferencesAsync(Resource resource, IDictionary<string, (string resourceId, string resourceType)> referenceIdDictionary, string requestUrl, CancellationToken cancellationToken)
        {
            IEnumerable<ResourceReference> references = resource.GetAllChildren<ResourceReference>();

            int totalResolvedReferences = 0;
            foreach (ResourceReference reference in references)
            {
                if (string.IsNullOrWhiteSpace(reference.Reference))
                {
                    continue;
                }

                string newReferenceValue = await TryResolveReferenceValueAsync(reference.Reference, referenceIdDictionary, requestUrl, cancellationToken);
                if (newReferenceValue != null)
                {
                    reference.Reference = newReferenceValue;
                    totalResolvedReferences++;
                }
            }

            return totalResolvedReferences;
        }

        /// <summary>
        /// Decides whether and how a single reference value should be rewritten: already-resolved
        /// (dictionary hit), conditional (resolved via search and cached), or left alone (plain/absolute
        /// reference, nothing to do). Shared by both the Firely loop above and <c>IgnixaResourceReferenceResolver</c>
        /// so there is exactly one implementation of this decision logic.
        /// </summary>
        /// <param name="reference">A non-null, non-whitespace reference string (e.g. "Patient/123", "Patient?identifier=...", or a "urn:uuid:" placeholder already seen).</param>
        /// <param name="referenceIdDictionary">Cache of previously-resolved conditional/placeholder references to their assigned type/id, shared and mutated across calls for a bundle.</param>
        /// <param name="requestUrl">The request URL, used only for error messages when a conditional reference can't be resolved.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved "Type/id" value to assign, or <c>null</c> if no resolution was needed (the caller should leave the original value alone).</returns>
        /// <exception cref="RequestNotValidException">The reference is conditional but the resource type is unsupported, or it resolves to zero or multiple matches.</exception>
        public async Task<string> TryResolveReferenceValueAsync(
            string reference,
            IDictionary<string, (string resourceId, string resourceType)> referenceIdDictionary,
            string requestUrl,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(reference, nameof(reference));
            EnsureArg.IsNotNull(referenceIdDictionary, nameof(referenceIdDictionary));

            // Checks to see if this reference has already been assigned an Id
            if (referenceIdDictionary.TryGetValue(reference, out var referenceInformation))
            {
                return $"{referenceInformation.resourceType}/{referenceInformation.resourceId}";
            }

            if (!reference.Contains('?', StringComparison.Ordinal))
            {
                return null;
            }

            string[] queries = reference.Split("?");
            string resourceType = queries[0];
            string conditionalQueries = queries[1];

            if (!ModelInfoProvider.IsKnownResource(resourceType))
            {
                throw new RequestNotValidException(string.Format(Core.Resources.ReferenceResourceTypeNotSupported, resourceType, reference));
            }

            var results = await GetExistingResourceId(requestUrl, resourceType, conditionalQueries, cancellationToken);

            if (results == null || !results.Where(result => result.SearchEntryMode == ValueSets.SearchEntryMode.Match).Any())
            {
                throw new RequestNotValidException(string.Format(Core.Resources.InvalidConditionalReference, reference));
            }
            else if (results.Where(result => result.SearchEntryMode == ValueSets.SearchEntryMode.Match).Count() > 1)
            {
                throw new RequestNotValidException(string.Format(Core.Resources.InvalidConditionalReferenceToMultipleResources, reference));
            }

            string resourceId = results.First(result => result.SearchEntryMode == ValueSets.SearchEntryMode.Match).Resource.ResourceId;

            referenceIdDictionary.Add(reference, (resourceId, resourceType));

            return $"{resourceType}/{resourceId}";
        }

        public async Task<IReadOnlyCollection<SearchResultEntry>> GetExistingResourceId(string requestUrl, string resourceType, StringValues conditionalQueries, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(resourceType) || string.IsNullOrEmpty(conditionalQueries))
            {
                throw new RequestNotValidException(string.Format(Core.Resources.InvalidConditionalReferenceParameters, requestUrl));
            }

            IReadOnlyList<Tuple<string, string>> conditionalParameters = _queryStringParser.Parse(conditionalQueries).AsTuples();

            var searchResourceRequest = new SearchResourceRequest(resourceType, conditionalParameters);

            var matches = (await _searchService.ConditionalSearchAsync(searchResourceRequest.ResourceType, searchResourceRequest.Queries, cancellationToken, logger: _logger))
                .Results
                .Where(result => result.SearchEntryMode == ValueSets.SearchEntryMode.Match)
                .ToList();

            return matches;
        }
    }
}
