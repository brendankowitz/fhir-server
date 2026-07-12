// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Health.Fhir.Core.Models;
using static Hl7.Fhir.Model.Bundle;

namespace Microsoft.Health.Fhir.Api.Features.Resources.Bundle
{
    /// <summary>
    /// <see cref="IBundleEntryView"/> over an Ignixa-native <see cref="BundleComponentJsonNode"/>.
    /// </summary>
    /// <remarks>
    /// Reads the conditional-header properties (<c>ifMatch</c>/<c>ifNoneMatch</c>/<c>ifModifiedSince</c>/
    /// <c>ifNoneExist</c>) directly off <see cref="BundleComponentRequestJsonNode"/>'s <c>MutableNode</c>,
    /// accessed via the <see cref="IMutableJsonNode"/> explicit-interface cast since the property itself
    /// is internal, because the typed surface exposes only <c>Method</c>/<c>Url</c> as of the
    /// pinned Ignixa SDK version -- tracked as an upstream gap (see
    /// docs/features/sdk-migration/ignixa-upstream-gaps.md). This mirrors the existing raw-node-property
    /// access pattern already used by <c>IgnixaImportResourceParser</c>.
    /// </remarks>
    public sealed class IgnixaBundleEntryView : IBundleEntryView
    {
        private readonly BundleComponentJsonNode _entry;

        public IgnixaBundleEntryView(BundleComponentJsonNode entry)
        {
            EnsureArg.IsNotNull(entry, nameof(entry));

            _entry = entry;
        }

        public HTTPVerb? Method
        {
            get
            {
                string rawMethod = _entry.Request?.Method;
                return string.IsNullOrEmpty(rawMethod) ? null : Enum.Parse<HTTPVerb>(rawMethod, ignoreCase: true);
            }
        }

        public string Url => _entry.Request?.Url;

        public string FullUrl => _entry.FullUrl;

        public string IfMatch => GetRequestHeaderProperty("ifMatch");

        public string IfNoneMatch => GetRequestHeaderProperty("ifNoneMatch");

        public string IfModifiedSince => GetRequestHeaderProperty("ifModifiedSince");

        public string IfNoneExist => GetRequestHeaderProperty("ifNoneExist");

        public bool HasResource => _entry.Resource != null;

        public string ResourceTypeName => _entry.Resource?.ResourceType;

        public string BinaryContentType =>
            string.Equals(ResourceTypeName, KnownResourceTypes.Binary, StringComparison.Ordinal)
                ? ((IMutableJsonNode)_entry.Resource).MutableNode["contentType"]?.GetValue<string>()
                : null;

        public async Task WriteResourceBodyAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (_entry.Resource != null)
            {
                await using var writer = new Utf8JsonWriter(stream);
                ((IMutableJsonNode)_entry.Resource).MutableNode.WriteTo(writer);
                await writer.FlushAsync(cancellationToken);
            }
        }

        public async Task WriteBinaryDataAsync(Stream stream, CancellationToken cancellationToken)
        {
            string base64Data = (_entry.Resource as IMutableJsonNode)?.MutableNode["data"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(base64Data))
            {
                byte[] bytes = Convert.FromBase64String(base64Data);
                await stream.WriteAsync(bytes, cancellationToken);
            }
        }

        private string GetRequestHeaderProperty(string name)
        {
            JsonObject requestNode = (_entry.Request as IMutableJsonNode)?.MutableNode;
            if (requestNode != null
                && requestNode.TryGetPropertyValue(name, out JsonNode value)
                && value is JsonValue jsonValue)
            {
                return jsonValue.GetValue<string>();
            }

            return null;
        }
    }
}
