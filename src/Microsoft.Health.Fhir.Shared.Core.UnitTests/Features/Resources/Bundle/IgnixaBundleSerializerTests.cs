// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources.Bundle;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

// Deliberately not "...Features.Resources.Bundle": a namespace segment named Bundle would shadow
// Hl7.Fhir.Model.Bundle for sibling test files in ...Features.Resources.
namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Resources;

[Trait(Traits.OwningTeam, OwningTeam.Fhir)]
[Trait(Traits.Category, Categories.Serialization)]
public class IgnixaBundleSerializerTests
{
    private static readonly DateTimeOffset _lastUpdated = new DateTimeOffset(2019, 1, 5, 15, 30, 23, TimeSpan.FromHours(8));

    private readonly IgnixaBundleSerializer _serializer = new IgnixaBundleSerializer();

    [Fact]
    public async Task GivenABundleWithMixedEntries_WhenSerialized_ThenOutputIsValidJsonWithCorrectStructure()
    {
        BundleJsonNode skeleton = CreateSkeleton("example", BundleJsonNode.BundleType.Searchset, total: 2);
        ((IMutableJsonNode)skeleton).MutableNode["link"] = new JsonArray(new JsonObject { ["relation"] = "self", ["url"] = "http://self/" });

        // A decoy entry on the skeleton proves the serializer treats Entries as authoritative.
        ((IMutableJsonNode)skeleton).MutableNode["entry"] = new JsonArray(new JsonObject { ["fullUrl"] = "http://decoy/" });

        var rawEntryMetadata = new BundleComponentJsonNode { FullUrl = "http://resource/123" };
        rawEntryMetadata.Search = new BundleComponentSearchJsonNode { Mode = "match" };
        RawResourceElement rawResource = CreateRawResourceElement(
            "{\"resourceType\":\"Observation\",\"id\":\"123\",\"status\":\"final\"}",
            version: "2",
            isMetaSet: false);

        var constructedEntryMetadata = new BundleComponentJsonNode();
        constructedEntryMetadata.Search = new BundleComponentSearchJsonNode { Mode = "outcome" };
        var operationOutcome = new ResourceJsonNode { ResourceType = "OperationOutcome", Id = "warning" };

        var requestOnlyMetadata = new BundleComponentJsonNode();
        requestOnlyMetadata.Request = new BundleComponentRequestJsonNode { Method = "DELETE", Url = "Observation/123" };

        var bundle = new IgnixaRawBundle(
            skeleton,
            new[]
            {
                IgnixaRawBundleEntry.ForRawResource(rawEntryMetadata, rawResource),
                IgnixaRawBundleEntry.ForConstructedResource(constructedEntryMetadata, operationOutcome),
                IgnixaRawBundleEntry.MetadataOnly(requestOnlyMetadata),
            });

        string output = await SerializeToString(bundle, pretty: false);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;

        Assert.Equal("Bundle", root.GetProperty("resourceType").GetString());
        Assert.Equal("example", root.GetProperty("id").GetString());
        Assert.Equal("searchset", root.GetProperty("type").GetString());
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal("http://self/", root.GetProperty("link")[0].GetProperty("url").GetString());

        JsonElement entries = root.GetProperty("entry");
        Assert.Equal(3, entries.GetArrayLength());

        JsonElement rawEntry = entries[0];
        Assert.Equal("http://resource/123", rawEntry.GetProperty("fullUrl").GetString());
        Assert.Equal("match", rawEntry.GetProperty("search").GetProperty("mode").GetString());
        Assert.Equal("123", rawEntry.GetProperty("resource").GetProperty("id").GetString());
        Assert.Equal("final", rawEntry.GetProperty("resource").GetProperty("status").GetString());

        JsonElement constructedEntry = entries[1];
        Assert.Equal("outcome", constructedEntry.GetProperty("search").GetProperty("mode").GetString());
        Assert.Equal("OperationOutcome", constructedEntry.GetProperty("resource").GetProperty("resourceType").GetString());
        Assert.Equal("warning", constructedEntry.GetProperty("resource").GetProperty("id").GetString());

        JsonElement requestOnlyEntry = entries[2];
        Assert.Equal("DELETE", requestOnlyEntry.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal("Observation/123", requestOnlyEntry.GetProperty("request").GetProperty("url").GetString());
        Assert.False(requestOnlyEntry.TryGetProperty("resource", out _));
    }

    [Fact]
    public async Task GivenARawResourceMissingMeta_WhenSerialized_ThenVersionIdAndLastUpdatedAreSynthesized()
    {
        BundleJsonNode skeleton = CreateSkeleton("meta-patching", BundleJsonNode.BundleType.Searchset, total: 1);
        var metadata = new BundleComponentJsonNode { FullUrl = "http://resource/abc" };
        RawResourceElement rawResource = CreateRawResourceElement(
            "{\"resourceType\":\"Observation\",\"id\":\"abc\",\"status\":\"final\"}",
            version: "3",
            isMetaSet: false);

        var bundle = new IgnixaRawBundle(skeleton, new[] { IgnixaRawBundleEntry.ForRawResource(metadata, rawResource) });

        string output = await SerializeToString(bundle, pretty: false);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement meta = document.RootElement.GetProperty("entry")[0].GetProperty("resource").GetProperty("meta");

        Assert.Equal("3", meta.GetProperty("versionId").GetString());
        Assert.Equal(_lastUpdated.ToInstantString(), meta.GetProperty("lastUpdated").GetString());
    }

    [Fact]
    public async Task GivenARawResourceEntryWithNoMetadata_WhenSerialized_ThenOutputIsValidJson()
    {
        BundleJsonNode skeleton = CreateSkeleton("no-metadata", BundleJsonNode.BundleType.History, total: 1);
        RawResourceElement rawResource = CreateRawResourceElement(
            "{\"resourceType\":\"Observation\",\"id\":\"bare\",\"status\":\"final\"}",
            version: "1",
            isMetaSet: false);

        var bundle = new IgnixaRawBundle(
            skeleton,
            new[] { IgnixaRawBundleEntry.ForRawResource(new BundleComponentJsonNode(), rawResource) });

        string output = await SerializeToString(bundle, pretty: false);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement entry = document.RootElement.GetProperty("entry")[0];

        Assert.Equal("bare", entry.GetProperty("resource").GetProperty("id").GetString());
        Assert.Single(entry.EnumerateObject());
    }

    [Fact]
    public async Task GivenPrettyRequested_WhenSerialized_ThenSkeletonIsIndentedButRawBodyPassesThroughUnchanged()
    {
        const string rawBody = "{\"resourceType\":\"Observation\",\"id\":\"x\",\"meta\":{\"versionId\":\"1\",\"lastUpdated\":\"2019-01-05T15:30:23+08:00\"},\"status\":\"final\"}";

        BundleJsonNode skeleton = CreateSkeleton("pretty", BundleJsonNode.BundleType.Searchset, total: 1);
        var metadata = new BundleComponentJsonNode { FullUrl = "http://resource/x" };
        RawResourceElement rawResource = CreateRawResourceElement(rawBody, version: "1", isMetaSet: true);

        var bundle = new IgnixaRawBundle(skeleton, new[] { IgnixaRawBundleEntry.ForRawResource(metadata, rawResource) });

        string output = await SerializeToString(bundle, pretty: true);

        using (JsonDocument.Parse(output))
        {
        }

        // Skeleton/metadata respect pretty (indented writer puts a space after the colon) ...
        Assert.Contains("\"resourceType\": \"Bundle\"", output, StringComparison.Ordinal);

        // ... but the spliced entry body passes through byte-for-byte, un-indented,
        // matching BundleSerializer's behavior of not propagating pretty into entry bodies.
        Assert.Contains(rawBody, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenABundleWithNoEntries_WhenSerialized_ThenEntryPropertyIsOmitted()
    {
        BundleJsonNode skeleton = CreateSkeleton("empty", BundleJsonNode.BundleType.Searchset, total: 0);

        var bundle = new IgnixaRawBundle(skeleton, Array.Empty<IgnixaRawBundleEntry>());

        string output = await SerializeToString(bundle, pretty: false);

        using JsonDocument document = JsonDocument.Parse(output);

        Assert.Equal(0, document.RootElement.GetProperty("total").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("entry", out _));
    }

    private static BundleJsonNode CreateSkeleton(string id, BundleJsonNode.BundleType type, int total)
    {
        return new BundleJsonNode
        {
            Id = id,
            Type = type,
            Total = total,
        };
    }

    private static RawResourceElement CreateRawResourceElement(string json, string version, bool isMetaSet)
    {
        var wrapper = new ResourceWrapper(
            "resource-id",
            version,
            "Observation",
            new RawResource(json, FhirResourceFormat.Json, isMetaSet),
            request: null,
            _lastUpdated,
            deleted: false,
            searchIndices: null,
            compartmentIndices: null,
            lastModifiedClaims: null);

        return new RawResourceElement(wrapper);
    }

    private async Task<string> SerializeToString(IgnixaRawBundle bundle, bool pretty)
    {
        using var stream = new MemoryStream();
        await _serializer.Serialize(bundle, stream, pretty);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
