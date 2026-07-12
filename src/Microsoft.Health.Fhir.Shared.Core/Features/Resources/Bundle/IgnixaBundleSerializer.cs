// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Extensions;

namespace Microsoft.Health.Fhir.Core.Features.Resources.Bundle
{
    /// <summary>
    /// Serializes an <see cref="IgnixaRawBundle"/> to a stream, generalizing the interleaved-writer
    /// zero-copy splice technique proven by <see cref="BundleSerializer"/>. The skeleton and each entry's
    /// metadata are written through a <see cref="Utf8JsonWriter"/>; each entry's resource body is either a
    /// complete DOM value written through the same writer (constructed bodies) or, for raw
    /// <see cref="Core.Models.RawResourceElement"/> bodies, spliced in verbatim via
    /// <see cref="RawResourceElementExtensions.SerializeToStreamAsUtf8Json"/> (which also patches
    /// meta.versionId/meta.lastUpdated). Because a raw splice writes bytes the Utf8JsonWriter is unaware
    /// of, the leading comma before a spliced "resource" property is emitted by hand.
    /// </summary>
    public sealed class IgnixaBundleSerializer
    {
        private readonly JsonWriterOptions _writerOptions = new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly JsonWriterOptions _indentedWriterOptions = new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = true,
        };

        public async Task Serialize(IgnixaRawBundle bundle, Stream outputStream, bool pretty = false)
        {
            EnsureArg.IsNotNull(bundle, nameof(bundle));
            EnsureArg.IsNotNull(outputStream, nameof(outputStream));

            await using Utf8JsonWriter writer = new Utf8JsonWriter(outputStream, pretty ? _indentedWriterOptions : _writerOptions);
            await using StreamWriter streamWriter = new StreamWriter(outputStream, leaveOpen: true);

            writer.WriteStartObject();

            // Skeleton's id/meta/type/total/link properties. Its own entry array (if any) is ignored --
            // bundle.Entries is authoritative.
            WriteProperties(writer, bundle.Skeleton.MutableNode, excludePropertyName: "entry");

            // Matching BundleSerializer, the entry array is omitted entirely when there are no entries
            // (FHIR JSON forbids empty arrays).
            if (bundle.Entries.Count > 0)
            {
                writer.WriteStartArray("entry");

                foreach (var entry in bundle.Entries)
                {
                    await WriteEntry(entry);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            await writer.FlushAsync();

            async Task WriteEntry(IgnixaRawBundleEntry entry)
            {
                writer.WriteStartObject();

                // Metadata's fullUrl/search/request/response. Any resource property is ignored -- the body
                // comes from RawResource/ResourceNode and is written last (below).
                int metadataPropertyCount = WriteProperties(writer, entry.Metadata.MutableNode, excludePropertyName: "resource");

                if (entry.RawResource != null)
                {
                    // The resource body is spliced verbatim through the StreamWriter, so the Utf8JsonWriter
                    // never sees the "resource" property. Flush it, then -- only if a metadata property
                    // preceded us -- emit the separating comma by hand (the writer would otherwise emit it
                    // when writing its next token, but its next token is WriteEndObject, which never emits a
                    // comma). Entry bodies are always spliced as UTF-8 and never re-indented for pretty mode,
                    // matching BundleSerializer.
                    await writer.FlushAsync();

                    if (metadataPropertyCount > 0)
                    {
                        await streamWriter.WriteAsync(",");
                    }

                    await streamWriter.WriteAsync("\"resource\":");
                    await streamWriter.FlushAsync();

                    await entry.RawResource.SerializeToStreamAsUtf8Json(outputStream);
                }
                else if (entry.ResourceNode != null)
                {
                    // Constructed body: already a complete value in the DOM, so write it through the writer,
                    // which handles the separating comma automatically.
                    writer.WritePropertyName("resource");
                    entry.ResourceNode.MutableNode.WriteTo(writer);
                }

                writer.WriteEndObject();
            }
        }

        private static int WriteProperties(Utf8JsonWriter writer, JsonObject node, string excludePropertyName)
        {
            int count = 0;

            foreach (var property in node)
            {
                if (string.Equals(property.Key, excludePropertyName, StringComparison.Ordinal))
                {
                    continue;
                }

                writer.WritePropertyName(property.Key);

                if (property.Value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }

                count++;
            }

            return count;
        }
    }
}
