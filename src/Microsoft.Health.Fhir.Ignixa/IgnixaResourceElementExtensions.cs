// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using EnsureThat;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Ignixa;

/// <summary>
/// Extension methods for <see cref="IgnixaResourceElement"/>.
/// </summary>
public static class IgnixaResourceElementExtensions
{
    /// <summary>
    /// Return true if this resource contains the Azure 'soft-deleted' extension in meta data.
    /// </summary>
    public static bool IsSoftDeleted(this IgnixaResourceElement resourceElement)
    {
        return resourceElement.Predicate(KnownFhirPaths.IsSoftDeletedExtension);
    }

    /// <summary>
    /// Converts an <see cref="IgnixaResourceElement"/> to a <see cref="ResourceElement"/> for compatibility with existing infrastructure.
    /// </summary>
    /// <param name="ignixaElement">The Ignixa resource element to convert.</param>
    /// <returns>A <see cref="ResourceElement"/> wrapping the same underlying data and storing the ResourceJsonNode for direct serialization.</returns>
    public static ResourceElement ToResourceElement(this IgnixaResourceElement ignixaElement)
    {
        // Pass the ResourceJsonNode as the resourceInstance so that RawResourceFactory
        // can detect it and serialize directly without POCO conversion.
        return new ResourceElement(ignixaElement.ToTypedElement(), ignixaElement.ResourceNode);
    }

    /// <summary>
    /// Creates a <see cref="ResourceWrapper"/> directly from an <see cref="IgnixaResourceElement"/>.
    /// </summary>
    /// <remarks>
    /// This extension method provides a more native API for creating resource wrappers from Ignixa elements.
    /// The underlying <see cref="Ignixa.Serialization.SourceNodes.ResourceJsonNode"/> is preserved for efficient
    /// serialization without POCO conversion, while the <see cref="Hl7.Fhir.ElementModel.ITypedElement"/> shim
    /// is used for search indexing.
    /// </remarks>
    /// <param name="factory">The resource wrapper factory.</param>
    /// <param name="resource">The Ignixa resource element to wrap.</param>
    /// <param name="deleted">A flag indicating whether the resource is deleted or not.</param>
    /// <param name="keepMeta">A flag indicating whether to keep the metadata section or clear it.</param>
    /// <param name="keepVersion">A flag indicating whether to keep the version or set it to 1.</param>
    /// <returns>An instance of <see cref="ResourceWrapper"/>.</returns>
    public static ResourceWrapper Create(
        this IResourceWrapperFactory factory,
        IgnixaResourceElement resource,
        bool deleted,
        bool keepMeta,
        bool keepVersion = false)
    {
        return factory.Create(resource.ToResourceElement(), deleted, keepMeta, keepVersion);
    }

    /// <summary>
    /// Rebuilds a fresh <see cref="ResourceElement"/> wrapper after <paramref name="resourceJsonNode"/>
    /// has been mutated in place.
    /// </summary>
    /// <remarks>
    /// After mutating a <see cref="ResourceJsonNode"/> held by a <see cref="ResourceElement"/>, always
    /// rebuild via this method rather than reusing the original <see cref="ResourceElement"/> instance.
    /// The original instance's cached <c>Instance</c>/<c>ToPoco()</c> views can go stale after in-place
    /// node mutation -- this bit a Phase 3 task in production
    /// (see docs/features/sdk-migration/node-mutation.md for the full rule and the two shapes of
    /// ResourceInstance that make this hazard non-obvious at the type level).
    /// Reuse-in-place is permitted ONLY when every downstream consumer of the returned value is proven
    /// to read exclusively via <c>GetIgnixaNode()</c> -- and that proof must be recorded in a comment
    /// at the call site, not assumed.
    /// </remarks>
    public static ResourceElement RebuildResourceElement(this ResourceJsonNode resourceJsonNode, IIgnixaSchemaContext schemaContext)
    {
        EnsureArg.IsNotNull(resourceJsonNode, nameof(resourceJsonNode));
        EnsureArg.IsNotNull(schemaContext, nameof(schemaContext));

        var ignixaElement = new IgnixaResourceElement(resourceJsonNode, schemaContext.Schema);
        return new ResourceElement(ignixaElement.ToTypedElement(), ignixaElement);
    }
}
