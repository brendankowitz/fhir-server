// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Hl7.Fhir.Model;
using MediatR;
using Microsoft.Build.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Messages.Create;
using Microsoft.Health.Fhir.Core.Messages.Upsert;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;

namespace Microsoft.Health.Fhir.Core.Features.Resources.Upsert
{
    /// <summary>
    /// Handles Conditional Update logic as defined in the spec https://www.hl7.org/fhir/http.html#cond-update
    /// </summary>
    public sealed class ConditionalUpsertResourceHandler : ConditionalResourceHandler<ConditionalUpsertResourceRequest, UpsertResourceResponse>
    {
        private readonly IMediator _mediator;
        private readonly IIgnixaSchemaContext _schemaContext;

        public ConditionalUpsertResourceHandler(
            IFhirDataStore fhirDataStore,
            Lazy<IConformanceProvider> conformanceProvider,
            IResourceWrapperFactory resourceWrapperFactory,
            ISearchService searchService,
            IMediator mediator,
            ResourceIdProvider resourceIdProvider,
            IAuthorizationService<DataActions> authorizationService,
            ILogger<ConditionalUpsertResourceHandler> logger,
            IIgnixaSchemaContext schemaContext)
            : base(searchService, fhirDataStore, conformanceProvider, resourceWrapperFactory, resourceIdProvider, authorizationService, logger)
        {
            EnsureArg.IsNotNull(mediator, nameof(mediator));
            EnsureArg.IsNotNull(schemaContext, nameof(schemaContext));

            _mediator = mediator;
            _schemaContext = schemaContext;
        }

        public override async Task<UpsertResourceResponse> HandleNoMatch(ConditionalUpsertResourceRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request.Resource.Id))
            {
                // No matches, no id provided: The server creates the resource
                // TODO: There is a potential contention issue here in that this could create another new resource with a different id
                return await _mediator.Send<UpsertResourceResponse>(new CreateResourceRequest(request.Resource, request.BundleResourceContext), cancellationToken);
            }
            else
            {
                // No matches, id provided: The server treats the interaction as an Update as Create interaction (or rejects it, if it does not support Update as Create)
                // TODO: There is a potential contention issue here that this could replace an existing resource
                return await _mediator.Send<UpsertResourceResponse>(new UpsertResourceRequest(request.Resource, request.BundleResourceContext), cancellationToken);
            }
        }

        public override async Task<UpsertResourceResponse> HandleSingleMatch(ConditionalUpsertResourceRequest request, SearchResultEntry match, CancellationToken cancellationToken)
        {
            ResourceWrapper resourceWrapper = match.Resource;
            var version = WeakETag.FromVersionId(resourceWrapper.Version);

            var resourceJsonNode = request.Resource.GetIgnixaNode();
            if (resourceJsonNode != null)
            {
                // Native path: stamp the id directly on the node, no POCO round-trip.
                if (string.IsNullOrEmpty(resourceJsonNode.Id) || string.Equals(resourceJsonNode.Id, resourceWrapper.ResourceId, StringComparison.Ordinal))
                {
                    // Mutate the shared node, then rebuild the wrapper - mirrors the native-path pattern
                    // in CreateResourceHandler/UpsertResourceHandler. See RebuildResourceElement for why
                    // reusing request.Resource directly here would leave that contract unmet.
                    resourceJsonNode.Id = resourceWrapper.ResourceId;
                    var updatedResource = resourceJsonNode.RebuildResourceElement(_schemaContext);
                    return await _mediator.Send<UpsertResourceResponse>(new UpsertResourceRequest(updatedResource, request.BundleResourceContext, version), cancellationToken);
                }
                else
                {
                    throw new BadRequestException(string.Format(Core.Resources.ConditionalUpdateMismatchedIds, resourceWrapper.ResourceId, resourceJsonNode.Id));
                }
            }

            // Fallback to POCO path for non-Ignixa resources
            Resource resource = request.Resource.ToPoco();

            if (string.IsNullOrEmpty(resource.Id) || string.Equals(resource.Id, resourceWrapper.ResourceId, StringComparison.Ordinal))
            {
                resource.Id = resourceWrapper.ResourceId;
                return await _mediator.Send<UpsertResourceResponse>(new UpsertResourceRequest(resource.ToResourceElement(), request.BundleResourceContext, version), cancellationToken);
            }
            else
            {
                throw new BadRequestException(string.Format(Core.Resources.ConditionalUpdateMismatchedIds, resourceWrapper.ResourceId, resource.Id));
            }
        }

        public override Task<bool> CheckAccess(CancellationToken cancellationToken)
        {
            return AuthorizationService.CheckConditionalUpdateAccess(cancellationToken);
        }
    }
}
