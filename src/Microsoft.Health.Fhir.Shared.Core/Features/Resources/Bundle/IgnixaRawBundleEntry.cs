// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using EnsureThat;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Resources.Bundle
{
    /// <summary>
    /// A single entry in an <see cref="IgnixaRawBundle"/>: bundle-entry metadata plus at most one resource
    /// body. The body is either a raw, unparsed <see cref="RawResourceElement"/> (zero-copy splice) or an
    /// in-process-constructed <see cref="ResourceJsonNode"/>, but never both; an entry may also carry
    /// neither (a metadata-only entry). Use the static factory methods to construct; they enforce the
    /// raw/constructed/neither invariant.
    /// </summary>
    public sealed class IgnixaRawBundleEntry
    {
        private IgnixaRawBundleEntry(BundleComponentJsonNode metadata, RawResourceElement rawResource, ResourceJsonNode resourceNode)
        {
            Metadata = metadata;
            RawResource = rawResource;
            ResourceNode = resourceNode;
        }

        /// <summary>fullUrl/search/request/response properties. Its resource property, if any, is ignored by the serializer.</summary>
        public BundleComponentJsonNode Metadata { get; }

        /// <summary>Non-null for entries whose body is a raw, unparsed resource read from storage (the common case -- search/history results).</summary>
        public RawResourceElement RawResource { get; }

        /// <summary>Non-null for entries whose body was constructed in-process (e.g. an OperationOutcome issue entry). Mutually exclusive with <see cref="RawResource"/>; entries may also have neither (a request-only entry, e.g. a batch error with no resource body).</summary>
        public ResourceJsonNode ResourceNode { get; }

        public static IgnixaRawBundleEntry ForRawResource(BundleComponentJsonNode metadata, RawResourceElement rawResource)
        {
            EnsureArg.IsNotNull(metadata, nameof(metadata));
            EnsureArg.IsNotNull(rawResource, nameof(rawResource));
            return new IgnixaRawBundleEntry(metadata, rawResource, null);
        }

        public static IgnixaRawBundleEntry ForConstructedResource(BundleComponentJsonNode metadata, ResourceJsonNode resourceNode)
        {
            EnsureArg.IsNotNull(metadata, nameof(metadata));
            EnsureArg.IsNotNull(resourceNode, nameof(resourceNode));
            return new IgnixaRawBundleEntry(metadata, null, resourceNode);
        }

        public static IgnixaRawBundleEntry MetadataOnly(BundleComponentJsonNode metadata)
        {
            EnsureArg.IsNotNull(metadata, nameof(metadata));
            return new IgnixaRawBundleEntry(metadata, null, null);
        }
    }
}
