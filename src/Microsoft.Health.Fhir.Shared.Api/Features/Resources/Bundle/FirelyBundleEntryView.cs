// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using static Hl7.Fhir.Model.Bundle;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Api.Features.Resources.Bundle
{
    /// <summary>
    /// <see cref="IBundleEntryView"/> over a Firely <see cref="EntryComponent"/>. A near-mechanical
    /// extraction of BundleHandler's original decomposition logic -- every member here reads exactly
    /// what <c>GenerateRequest</c>/<c>FillRequestLists</c> used to read directly off the entry.
    /// </summary>
    public sealed class FirelyBundleEntryView : IBundleEntryView
    {
        private readonly EntryComponent _entry;
        private readonly FhirJsonSerializer _fhirJsonSerializer;

        public FirelyBundleEntryView(EntryComponent entry, FhirJsonSerializer fhirJsonSerializer)
        {
            EnsureArg.IsNotNull(entry, nameof(entry));
            EnsureArg.IsNotNull(fhirJsonSerializer, nameof(fhirJsonSerializer));

            _entry = entry;
            _fhirJsonSerializer = fhirJsonSerializer;
        }

        /// <summary>
        /// The wrapped Firely entry. Exposed internally only for <c>GenerateRequest</c>'s
        /// transaction-only reference-resolution branch, which needs direct POCO <see cref="Resource"/>
        /// access (the resolver mutates it in place) -- deliberately not part of
        /// <see cref="IBundleEntryView"/>, since <see cref="IgnixaBundleEntryView"/> entries never reach
        /// that branch under the current (batch-only) scope.
        /// </summary>
        internal EntryComponent Entry => _entry;

        public HTTPVerb? Method => _entry.Request?.Method;

        public string Url => _entry.Request?.Url;

        public string FullUrl => _entry.FullUrl;

        public string IfMatch => _entry.Request?.IfMatch;

        public string IfNoneMatch => _entry.Request?.IfNoneMatch;

        public string IfModifiedSince => _entry.Request?.IfModifiedSince?.ToString();

        public string IfNoneExist => _entry.Request?.IfNoneExist;

        public bool HasResource => _entry.Resource != null;

        public string ResourceTypeName => _entry.Resource?.TypeName;

        public string BinaryContentType => _entry.Resource is Binary binary ? binary.ContentType : null;

        public async Task WriteResourceBodyAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] bytes = await _fhirJsonSerializer.SerializeToBytesAsync(_entry.Resource);
            await stream.WriteAsync(bytes, cancellationToken);
        }

        public async Task WriteBinaryDataAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (_entry.Resource is Binary binary && binary.Data != null)
            {
                await stream.WriteAsync(binary.Data, cancellationToken);
            }
        }
    }
}
