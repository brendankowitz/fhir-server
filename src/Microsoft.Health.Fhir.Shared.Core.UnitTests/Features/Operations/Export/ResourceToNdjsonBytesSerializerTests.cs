// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Linq;
using System.Text;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.FhirPath;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Operations.Export;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Operations.Export
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Export)]
    public class ResourceToNdjsonBytesSerializerTests
    {
        private readonly ResourceDeserializer _resourceDeserializaer;
        private readonly FhirJsonParser _jsonParser = new FhirJsonParser();
        private readonly FhirXmlParser _xmlParser = new FhirXmlParser();

        private readonly ResourceToNdjsonBytesSerializer _serializer;
        private readonly IIgnixaJsonSerializer _ignixaSerializer;

        private readonly Observation _resource;
        private readonly byte[] _expectedBytes;

        public ResourceToNdjsonBytesSerializerTests()
        {
            _resourceDeserializaer = new ResourceDeserializer(
                (FhirResourceFormat.Json, new Func<string, string, DateTimeOffset, ResourceElement>((str, version, lastModified) => _jsonParser.Parse<Resource>(str).ToResourceElement())),
                (FhirResourceFormat.Xml, new Func<string, string, DateTimeOffset, ResourceElement>((str, version, lastModified) => _xmlParser.Parse<Resource>(str).ToResourceElement())));

            _ignixaSerializer = new IgnixaJsonSerializer();

            _serializer = new ResourceToNdjsonBytesSerializer(_ignixaSerializer);

            _resource = Samples.GetDefaultObservation().ToPoco<Observation>();
            _resource.Id = "test";

            // Expected bytes use Firely serialization format since the test deserializer
            // creates Firely-based ResourceElements without Ignixa nodes (legacy fallback path)
            string firelyJson = new FhirJsonSerializer().SerializeToString(_resource);
            string expectedString = $"{firelyJson}\n";

            _expectedBytes = Encoding.UTF8.GetBytes(expectedString);
        }

        [Fact]
        public void GivenARawResourceInJsonFormat_WhenSerialized_ThenCorrectByteArrayShouldBeProduced()
        {
            var rawResource = new RawResource(
                new FhirJsonSerializer().SerializeToString(_resource),
                FhirResourceFormat.Json,
                isMetaSet: false);

            ResourceWrapper resourceWrapper = CreateResourceWrapper(rawResource);
            ResourceElement element = _resourceDeserializaer.DeserializeRaw(resourceWrapper.RawResource, resourceWrapper.Version, resourceWrapper.LastModified);

            byte[] actualBytes = _serializer.Serialize(element);

            Assert.Equal(_expectedBytes, actualBytes);
        }

        [Fact]
        public void GivenAInvalidElementNode_WhenSerialized_ByteArrayShouldBeProduced()
        {
            var node = ElementNode.FromElement(_resource.ToTypedElement());
            (((ScopedNode)node.Select("Observation.text").First()).Current as ElementNode).Value = "invalid";
            var newElement = new ResourceElement(node);
            Assert.Throws<FormatException>(() => newElement.Instance.ToPoco<Resource>().ToJson());

            Assert.Equal(Samples.GetInvalidResourceJson().Replace("\r\n", "\n"), Encoding.UTF8.GetString(_serializer.Serialize(newElement)).Replace("\r\n", "\n"));
        }

        [Fact]
        public void GivenARawResourceInXmlFormat_WhenSerialized_ThenCorrectByteArrayShouldBeProduced()
        {
            var rawResource = new RawResource(
                new FhirXmlSerializer().SerializeToString(_resource),
                FhirResourceFormat.Xml,
                isMetaSet: false);

            ResourceWrapper resourceWrapper = CreateResourceWrapper(rawResource);
            ResourceElement element = _resourceDeserializaer.DeserializeRaw(resourceWrapper.RawResource, resourceWrapper.Version, resourceWrapper.LastModified);

            byte[] actualBytes = _serializer.Serialize(element);

            Assert.Equal(_expectedBytes, actualBytes);
        }

        [Fact]
        public void GivenAnIgnixaBackedResourceWithoutSoftDeletedExtension_WhenSerializedWithAddSoftDeletedExtension_ThenExtensionIsAddedAndIgnixaSerializerIsUsed()
        {
            ResourceElement nodeBackedElement = CreateIgnixaBackedElement();

            string actual = _serializer.StringSerialize(nodeBackedElement, addSoftDeletedExtension: true);

            // The native branch mutates the node in place and returns the same ResourceElement, so
            // SerializeToJson's GetIgnixaNode() check should still succeed -- meaning the Ignixa serializer,
            // not the Firely fallback, produced this string. Comparing against the Ignixa serializer's own
            // output for the (now-mutated) node is a direct way to assert which path was taken.
            Assert.NotNull(nodeBackedElement.GetIgnixaNode());
            string expected = _ignixaSerializer.Serialize(nodeBackedElement.GetIgnixaNode(), pretty: false);
            Assert.Equal(expected, actual);

            Assert.Contains(KnownFhirPaths.AzureSoftDeletedExtensionUrl, actual, StringComparison.Ordinal);
        }

        [Fact]
        public void GivenAnIgnixaBackedResource_WhenAddSoftDeletedExtensionCalledTwice_ThenExtensionIsNotDuplicated()
        {
            ResourceElement nodeBackedElement = CreateIgnixaBackedElement();

            _serializer.StringSerialize(nodeBackedElement, addSoftDeletedExtension: true);
            string actual = _serializer.StringSerialize(nodeBackedElement, addSoftDeletedExtension: true);

            int occurrences = actual.Split(KnownFhirPaths.AzureSoftDeletedExtensionUrl, StringSplitOptions.None).Length - 1;
            Assert.Equal(1, occurrences);
        }

        [Fact]
        public void GivenAFirelyBackedResource_WhenSerializedWithAddSoftDeletedExtension_ThenExistingPocoFallbackPathIsUnchanged()
        {
            ResourceElement firelyBackedElement = _resource.ToResourceElement();
            Assert.Null(firelyBackedElement.GetIgnixaNode());

            // Compute the expected JSON from an independent deep copy *before* invoking the mutating call:
            // Firely's ITypedElement.ToPoco<T>() unwraps back to the same POCO instance that was wrapped by
            // ToTypedElement(), so the POCO fallback path in TryAddSoftDeletedExtension mutates _resource's
            // own Meta.Extension list in place. Copying afterwards would observe the already-mutated state.
            Observation expectedResource = (Observation)_resource.DeepCopy();
            expectedResource.Meta ??= new Meta();
            expectedResource.Meta.Extension.Add(
                new Extension
                {
                    Url = KnownFhirPaths.AzureSoftDeletedExtensionUrl,
                    Value = new FhirString("soft-deleted"),
                });
            string expected = new FhirJsonSerializer().SerializeToString(expectedResource);

            string actual = _serializer.StringSerialize(firelyBackedElement, addSoftDeletedExtension: true);

            Assert.Equal(expected, actual);
        }

        private ResourceElement CreateIgnixaBackedElement()
        {
            var serializer = new IgnixaJsonSerializer();
            var schemaContext = new IgnixaSchemaContext(ModelInfoProvider.Instance);
            var ignixaElement = new IgnixaResourceElement(serializer.Parse(new FhirJsonSerializer().SerializeToString(_resource)), schemaContext.Schema);

            ResourceElement nodeBackedElement = ignixaElement.ToResourceElement();
            Assert.NotNull(nodeBackedElement.GetIgnixaNode());

            return nodeBackedElement;
        }

        private ResourceWrapper CreateResourceWrapper(RawResource rawResource)
        {
            return new ResourceWrapper(
                _resource.ToResourceElement(),
                rawResource,
                null,
                false,
                null,
                null,
                null);
        }
    }
}
