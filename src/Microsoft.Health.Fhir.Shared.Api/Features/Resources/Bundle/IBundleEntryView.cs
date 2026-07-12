// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static Hl7.Fhir.Model.Bundle;

namespace Microsoft.Health.Fhir.Api.Features.Resources.Bundle
{
    /// <summary>
    /// A read-only view over a single bundle entry's request/resource data, abstracting away whether
    /// the underlying bundle was parsed into Firely POCOs (<see cref="FirelyBundleEntryView"/>) or is an
    /// Ignixa-native bundle (<see cref="IgnixaBundleEntryView"/>). Lets <see cref="BundleHandler"/>'s
    /// entry-decomposition logic (<c>FillRequestLists</c>/<c>GenerateRequest</c>) be written once
    /// against a single shape instead of forked per SDK.
    /// </summary>
    public interface IBundleEntryView
    {
        /// <summary>
        /// <c>entry.request.method</c>. Null when the entry has no request at all (an empty/malformed
        /// entry) -- callers skip these rather than calling <c>GenerateRequest</c>.
        /// </summary>
        HTTPVerb? Method { get; }

        /// <summary><c>entry.request.url</c>.</summary>
        [SuppressMessage("Design", "CA1056", Justification = "Mirrors the FHIR-spec-typed string on Bundle.RequestComponent.Url/BundleComponentRequestJsonNode.Url -- both underlying representations are plain strings, not Uri.")]
        string Url { get; }

        /// <summary><c>entry.fullUrl</c>.</summary>
        [SuppressMessage("Design", "CA1056", Justification = "Mirrors the FHIR-spec-typed string on Bundle.EntryComponent.FullUrl/BundleComponentJsonNode.FullUrl -- both underlying representations are plain strings, not Uri.")]
        string FullUrl { get; }

        /// <summary><c>entry.request.ifMatch</c>.</summary>
        string IfMatch { get; }

        /// <summary><c>entry.request.ifNoneMatch</c>.</summary>
        string IfNoneMatch { get; }

        /// <summary>
        /// <c>entry.request.ifModifiedSince</c>, already formatted exactly as it should appear on the
        /// outgoing header.
        /// </summary>
        string IfModifiedSince { get; }

        /// <summary><c>entry.request.ifNoneExist</c>.</summary>
        string IfNoneExist { get; }

        /// <summary>True when the entry carries a <c>resource</c>.</summary>
        bool HasResource { get; }

        /// <summary>
        /// The attached resource's type name (e.g. "Patient", "Binary", "Parameters"). Null when
        /// <see cref="HasResource"/> is false.
        /// </summary>
        string ResourceTypeName { get; }

        /// <summary>
        /// The attached <c>Binary</c> resource's own <c>contentType</c> element, verbatim. Only
        /// non-null when <see cref="ResourceTypeName"/> is "Binary".
        /// </summary>
        string BinaryContentType { get; }

        /// <summary>
        /// FHIR-JSON-serializes the attached resource to <paramref name="stream"/> -- the common
        /// create/update body shape, and also the Parameters-FHIRPatch body shape (a Parameters
        /// resource is serialized the same way as any other resource).
        /// </summary>
        Task WriteResourceBodyAsync(Stream stream, CancellationToken cancellationToken);

        /// <summary>
        /// Writes the attached <c>Binary</c> resource's decoded <c>data</c> bytes to
        /// <paramref name="stream"/> -- the Binary-wrapped-JSON-Patch body shape.
        /// </summary>
        Task WriteBinaryDataAsync(Stream stream, CancellationToken cancellationToken);
    }
}
