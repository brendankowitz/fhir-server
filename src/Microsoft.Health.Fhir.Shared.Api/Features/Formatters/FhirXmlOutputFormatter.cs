// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using EnsureThat;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Ignixa.Serialization.SourceNodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Health.Fhir.Api.Features.ContentTypes;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources.Bundle;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Fhir.Shared.Core.Features.Search;
using Newtonsoft.Json;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Api.Features.Formatters
{
    internal class FhirXmlOutputFormatter : TextOutputFormatter
    {
        private static readonly FhirJsonParser JsonParser = new FhirJsonParser();

        private readonly FhirXmlSerializer _fhirXmlSerializer;
        private readonly ResourceDeserializer _deserializer;
        private readonly IModelInfoProvider _modelInfoProvider;
        private readonly IIgnixaJsonSerializer _ignixaSerializer;
        private readonly IgnixaBundleSerializer _ignixaBundleSerializer;

        public FhirXmlOutputFormatter(
            FhirXmlSerializer fhirXmlSerializer,
            ResourceDeserializer deserializer,
            IModelInfoProvider modelInfoProvider,
            IIgnixaJsonSerializer ignixaSerializer,
            IgnixaBundleSerializer ignixaBundleSerializer)
        {
            EnsureArg.IsNotNull(fhirXmlSerializer, nameof(fhirXmlSerializer));
            EnsureArg.IsNotNull(deserializer, nameof(deserializer));
            EnsureArg.IsNotNull(modelInfoProvider, nameof(modelInfoProvider));
            EnsureArg.IsNotNull(ignixaSerializer, nameof(ignixaSerializer));
            EnsureArg.IsNotNull(ignixaBundleSerializer, nameof(ignixaBundleSerializer));

            _fhirXmlSerializer = fhirXmlSerializer;
            _deserializer = deserializer;
            _modelInfoProvider = modelInfoProvider;
            _ignixaSerializer = ignixaSerializer;
            _ignixaBundleSerializer = ignixaBundleSerializer;

            SupportedEncodings.Add(Encoding.UTF8);
            SupportedEncodings.Add(Encoding.Unicode);

            SupportedMediaTypes.Add(KnownContentTypes.XmlContentType);
            SupportedMediaTypes.Add(KnownMediaTypeHeaderValues.ApplicationXml);
            SupportedMediaTypes.Add(KnownMediaTypeHeaderValues.TextXml);
            SupportedMediaTypes.Add(KnownMediaTypeHeaderValues.ApplicationAnyXmlSyntax);
        }

        protected override bool CanWriteType(Type type)
        {
            EnsureArg.IsNotNull(type, nameof(type));

            return typeof(Resource).IsAssignableFrom(type) ||
                   typeof(RawResourceElement).IsAssignableFrom(type) ||
                   typeof(ResourceJsonNode).IsAssignableFrom(type) ||
                   typeof(IgnixaRawBundle).IsAssignableFrom(type);
        }

        public override async Task WriteResponseBodyAsync(OutputFormatterWriteContext context, Encoding selectedEncoding)
        {
            EnsureArg.IsNotNull(context, nameof(context));
            EnsureArg.IsNotNull(selectedEncoding, nameof(selectedEncoding));

            context.HttpContext.AllowSynchronousIO();

            var elementsSearchParameter = context.HttpContext.GetElementsOrDefault();
            var hasElements = elementsSearchParameter?.Any() == true;
            var summaryProvider = _modelInfoProvider.StructureDefinitionSummaryProvider;
            var additionalElements = new HashSet<string>();

            void AddRequiredElements(Resource resource)
            {
                if (!hasElements || resource == null)
                {
                    return;
                }

                var typeinfo = summaryProvider.Provide(resource.TypeName);
                var required = typeinfo.GetElements().Where(e => e.IsRequired).ToList();
                additionalElements.UnionWith(required.Select(x => x.ElementName));
            }

            Resource resourceObject;
            if (typeof(RawResourceElement).IsAssignableFrom(context.ObjectType))
            {
                resourceObject = _deserializer.Deserialize(context.Object as RawResourceElement).ToPoco<Resource>();
                AddRequiredElements(resourceObject);
            }
            else if (context.Object is IgnixaRawBundle ignixaRawBundle)
            {
                // IgnixaRawBundle can only be converted via IgnixaBundleSerializer -- see the type's own
                // remarks on why a generic ToPoco()/skeleton-only conversion would silently produce a
                // hollow entry array. This mirrors IgnixaFhirJsonOutputFormatter's ToFirelyBundleAsync.
                var bundle = await ConvertToFirelyBundleAsync(ignixaRawBundle).ConfigureAwait(false);

                foreach (var entry in bundle.Entry)
                {
                    AddRequiredElements(entry.Resource);
                }

                resourceObject = bundle;
            }
            else if (context.Object is ResourceJsonNode resourceNode)
            {
                resourceObject = ConvertToFirelyResource(resourceNode);
                AddRequiredElements(resourceObject);
            }
            else if (typeof(Hl7.Fhir.Model.Bundle).IsAssignableFrom(context.ObjectType))
            {
                // Need to set Resource property for resources in entries
                var bundle = context.Object as Hl7.Fhir.Model.Bundle;

                foreach (var entry in bundle.Entry.Where(x => x is RawBundleEntryComponent))
                {
                    var rawResource = entry as RawBundleEntryComponent;
                    entry.Resource = _deserializer.Deserialize(rawResource.ResourceElement).ToPoco<Resource>();
                    AddRequiredElements(entry.Resource);
                }

                resourceObject = bundle;
            }
            else
            {
                resourceObject = (Resource)context.Object;
                AddRequiredElements(resourceObject);
            }

            if (hasElements)
            {
                additionalElements.UnionWith(elementsSearchParameter);
                additionalElements.Add("meta");
            }

            HttpResponse response = context.HttpContext.Response;
            using (TextWriter textWriter = context.WriterFactory(response.Body, selectedEncoding))
            using (var writer = new XmlTextWriter(textWriter))
            {
                if (context.HttpContext.GetPrettyOrDefault())
                {
                    writer.Formatting = System.Xml.Formatting.Indented;
                }

                // I'll be happy to call async method here, but it crashes internally on call to XmlReader which doesn't implement
                // async version of certain methods.
#pragma warning disable CA1849 // Call async methods when in an async method
                _fhirXmlSerializer.Serialize(resourceObject, writer, context.HttpContext.GetSummaryTypeOrDefault(), elements: hasElements ? additionalElements.ToArray() : null);
#pragma warning restore CA1849 // Call async methods when in an async method
            }
        }

        /// <summary>
        /// Converts a bare <see cref="ResourceJsonNode"/> to a Firely <see cref="Resource"/> POCO by
        /// serializing it to JSON via the injected <see cref="IIgnixaJsonSerializer"/> and re-parsing with
        /// <see cref="FhirJsonParser"/>. Avoids adding an <c>IIgnixaSchemaContext</c> dependency just for
        /// this conversion.
        /// </summary>
        private Resource ConvertToFirelyResource(ResourceJsonNode resourceNode)
        {
            var json = _ignixaSerializer.Serialize(resourceNode, pretty: false);
            return JsonParser.Parse<Resource>(json);
        }

        /// <summary>
        /// Converts an <see cref="IgnixaRawBundle"/> to a Firely <see cref="Hl7.Fhir.Model.Bundle"/> POCO by
        /// round-tripping it through <see cref="IgnixaBundleSerializer"/> (the only supported way to
        /// materialize this carrier's entries) and re-parsing with <see cref="FhirJsonParser"/>.
        /// </summary>
        private async System.Threading.Tasks.Task<Hl7.Fhir.Model.Bundle> ConvertToFirelyBundleAsync(IgnixaRawBundle rawBundle)
        {
            return await IgnixaBundleConversion.ToFirelyBundleAsync(_ignixaBundleSerializer, rawBundle).ConfigureAwait(false);
        }
    }
}
