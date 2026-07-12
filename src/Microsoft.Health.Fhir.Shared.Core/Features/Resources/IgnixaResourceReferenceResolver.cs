// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Ignixa;

namespace Microsoft.Health.Fhir.Core.Features.Resources
{
    /// <summary>
    /// Resolves intra-bundle and conditional references in place on an Ignixa node-backed resource.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="IgnixaReferenceScanner"/> to find Reference-typed elements by schema type
    /// (not JSON property name -- see the scanner's remarks for why that distinction matters), and
    /// delegates the actual resolve/cache/throw decision to
    /// <see cref="ResourceReferenceResolver.TryResolveReferenceValueAsync"/> so this class and the
    /// Firely-POCO path share exactly one implementation of that decision logic.
    /// </remarks>
    public class IgnixaResourceReferenceResolver
    {
        private readonly ResourceReferenceResolver _coreResolver;
        private readonly IIgnixaSchemaContext _schemaContext;

        public IgnixaResourceReferenceResolver(ResourceReferenceResolver coreResolver, IIgnixaSchemaContext schemaContext)
        {
            _coreResolver = EnsureArg.IsNotNull(coreResolver, nameof(coreResolver));
            _schemaContext = EnsureArg.IsNotNull(schemaContext, nameof(schemaContext));
        }

        /// <summary>
        /// Scans <paramref name="resource"/> for Reference-typed elements and rewrites each resolvable
        /// one in place.
        /// </summary>
        /// <param name="resource">The resource to scan and mutate.</param>
        /// <param name="referenceIdDictionary">Cache of previously-resolved conditional/placeholder references, shared and mutated across calls for a bundle.</param>
        /// <param name="requestUrl">The request URL, used only for error messages when a conditional reference can't be resolved.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of references rewritten.</returns>
        public async Task<int> ResolveReferencesAsync(
            ResourceJsonNode resource,
            IDictionary<string, (string resourceId, string resourceType)> referenceIdDictionary,
            string requestUrl,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            EnsureArg.IsNotNull(referenceIdDictionary, nameof(referenceIdDictionary));

            int resolvedCount = 0;
            foreach (IgnixaReferenceHandle handle in IgnixaReferenceScanner.EnumerateReferences(resource.ToElement(_schemaContext.Schema)))
            {
                if (string.IsNullOrWhiteSpace(handle.Reference))
                {
                    continue;
                }

                string newValue = await _coreResolver.TryResolveReferenceValueAsync(handle.Reference, referenceIdDictionary, requestUrl, cancellationToken);
                if (newValue != null)
                {
                    handle.SetReference(newValue);
                    resolvedCount++;
                }
            }

            if (resolvedCount > 0)
            {
                resource.InvalidateCaches();
            }

            return resolvedCount;
        }
    }
}
