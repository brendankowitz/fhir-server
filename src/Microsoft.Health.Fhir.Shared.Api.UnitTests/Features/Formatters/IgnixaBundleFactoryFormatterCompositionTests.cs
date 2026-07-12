// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Api.Features.ActionResults;
using Microsoft.Health.Fhir.Api.Features.Formatters;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources.Bundle;
using Microsoft.Health.Fhir.Core.Features.Routing;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;
using static Hl7.Fhir.Model.Bundle;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Api.UnitTests.Features.Formatters
{
    /// <summary>
    /// End-to-end composition coverage for the Hybrid/Ignixa search-bundle path that Phase 4 Plan A activated:
    /// a bundle produced by the REAL <see cref="IgnixaBundleFactory"/> is unwrapped by the REAL
    /// <see cref="FhirResult"/> and serialized by the REAL JSON/XML output formatters -- the
    /// <c>SearchResourceHandler -> IgnixaBundleFactory -> FhirResult -> formatter</c> chain minus the datastore.
    /// The per-component tests (<c>IgnixaBundleFactoryTests</c>, <c>IgnixaFhirJsonOutputFormatterTests</c>,
    /// <c>FhirXmlOutputFormatterTests</c>) exercise each seam against hand-built carriers; this file proves the
    /// factory's ACTUAL output (fullUrl via <see cref="IUrlResolver"/>, meta-patched raw entry bodies, a skeleton
    /// carrying <c>Meta.LastUpdated</c>/<c>Id</c>) survives the projection round-trip and XML conversion those
    /// tests only approximate.
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public class IgnixaBundleFactoryFormatterCompositionTests
    {
        private static readonly FhirJsonParser JsonParser = new FhirJsonParser();
        private static readonly FhirXmlParser XmlParser = new FhirXmlParser();

        private const string ResourceUrlFormat = "http://resource/{0}";

        private readonly IUrlResolver _urlResolver = Substitute.For<IUrlResolver>();
        private readonly RequestContextAccessor<IFhirRequestContext> _fhirRequestContextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
        private readonly IgnixaBundleFactory _factory;

        public IgnixaBundleFactoryFormatterCompositionTests()
        {
            _urlResolver.ResolveResourceWrapperUrl(Arg.Any<ResourceWrapper>()).Returns(x => new Uri(string.Format(ResourceUrlFormat, x.ArgAt<ResourceWrapper>(0).ResourceId)));
            _urlResolver.ResolveRouteUrl(Arg.Any<IReadOnlyList<Tuple<string, string>>>()).Returns(new Uri("http://self/"));

            IFhirRequestContext requestContext = Substitute.For<IFhirRequestContext>();
            requestContext.CorrelationId.Returns(Guid.NewGuid().ToString());
            requestContext.BundleIssues.Returns(new List<OperationOutcomeIssue>());
            _fhirRequestContextAccessor.RequestContext.Returns(requestContext);

            _factory = new IgnixaBundleFactory(
                _urlResolver,
                _fhirRequestContextAccessor,
                new IgnixaSchemaContext(ModelInfoProvider.Instance),
                NullLogger<IgnixaBundleFactory>.Instance);
        }

        [Fact]
        public async Task GivenAFactoryProducedSearchBundle_WhenServedAsJson_ThenAllEntriesArePresent()
        {
            object toSerialize = ResultToSerialize(_factory.CreateSearchBundle(CreateSearchResult("123", "abc")));

            string json = await WriteJson(toSerialize, query: null);

            var parsed = JsonParser.Parse<Hl7.Fhir.Model.Bundle>(json);
            Assert.Equal(BundleType.Searchset, parsed.Type);
            Assert.Equal(2, parsed.Entry.Count);
            Assert.All(parsed.Entry, entry => Assert.NotNull(entry.Resource));
            Assert.Equal(new[] { "123", "abc" }, parsed.Entry.Select(e => e.Resource.Id));
        }

        [Fact]
        public async Task GivenAFactoryProducedSearchBundle_WhenServedAsXml_ThenAllEntriesArePresent()
        {
            object toSerialize = ResultToSerialize(_factory.CreateSearchBundle(CreateSearchResult("123", "abc")));

            string xml = await WriteXml(toSerialize);

            var parsed = XmlParser.Parse<Hl7.Fhir.Model.Bundle>(xml);
            Assert.Equal(BundleType.Searchset, parsed.Type);
            Assert.Equal(2, parsed.Entry.Count);
            Assert.All(parsed.Entry, entry => Assert.NotNull(entry.Resource));
            Assert.Equal(new[] { "123", "abc" }, parsed.Entry.Select(e => e.Resource.Id));
        }

        [Fact]
        public async Task GivenAFactoryProducedSearchBundle_WhenServedWithSummaryCount_ThenEntriesAreOmittedButTotalRemains()
        {
            SearchResult searchResult = CreateSearchResult("123", "abc");
            searchResult.TotalCount = 2;

            object toSerialize = ResultToSerialize(_factory.CreateSearchBundle(searchResult));

            string json = await WriteJson(toSerialize, query: "?_summary=count");

            var parsed = JsonParser.Parse<Hl7.Fhir.Model.Bundle>(json);
            Assert.Empty(parsed.Entry);
            Assert.Equal(2, parsed.Total);
        }

        [Fact]
        public async Task GivenAFactoryProducedSearchBundle_WhenServedWithElements_ThenEachEntryIsProjected()
        {
            var patient = new Patient
            {
                Id = "patient-elements",
                Active = true,
                Name = { new HumanName { Family = "Smith", Given = new[] { "John" } } },
            };

            var searchResult = CreateSearchResultFrom(patient.ToResourceElement());

            object toSerialize = ResultToSerialize(_factory.CreateSearchBundle(searchResult));

            string json = await WriteJson(toSerialize, query: "?_elements=active");

            var parsed = JsonParser.Parse<Hl7.Fhir.Model.Bundle>(json);
            var entry = Assert.Single(parsed.Entry);
            var parsedPatient = Assert.IsType<Patient>(entry.Resource);

            // _elements keeps the requested element (active) plus mandatory ones; the non-requested,
            // non-mandatory 'name' element present on the source Patient must be projected away.
            Assert.True(parsedPatient.Active);
            Assert.Empty(parsedPatient.Name);
        }

        [Fact]
        public async Task GivenAFactoryProducedHistoryBundle_WhenServedAsJson_ThenEntryHasRequestAndResponseMetadata()
        {
            object toSerialize = ResultToSerialize(_factory.CreateHistoryBundle(CreateSearchResult("hist-1")));

            string json = await WriteJson(toSerialize, query: null);

            var parsed = JsonParser.Parse<Hl7.Fhir.Model.Bundle>(json);
            Assert.Equal(BundleType.History, parsed.Type);
            var entry = Assert.Single(parsed.Entry);
            Assert.NotNull(entry.Request);
            Assert.NotNull(entry.Response);
            Assert.Equal(HTTPVerb.POST, entry.Request.Method);
        }

        private SearchResult CreateSearchResult(params string[] ids)
        {
            var resources = ids
                .Select(id => Samples.GetDefaultObservation().UpdateId(id).UpdateVersion("1"))
                .ToArray();

            return CreateSearchResultFrom(resources);
        }

        private SearchResult CreateSearchResultFrom(params ResourceElement[] resources)
        {
            var entries = resources
                .Select(resource => new SearchResultEntry(CreateResourceWrapper(resource)))
                .ToArray();

            return new SearchResult(entries, continuationToken: null, sortOrder: null, unsupportedSearchParameters: Array.Empty<Tuple<string, string>>());
        }

        private static ResourceWrapper CreateResourceWrapper(ResourceElement resourceElement)
        {
            return new ResourceWrapper(
                resourceElement,
                new RawResource(new FhirJsonSerializer().SerializeToString(resourceElement.ToPoco()), FhirResourceFormat.Json, isMetaSet: false),
                new ResourceRequest(HttpMethod.Post, url: "http://test/Resource/id"),
                deleted: false,
                null,
                null,
                null)
            {
                Version = "1",
            };
        }

        private static object ResultToSerialize(ResourceElement bundleElement)
        {
            var fhirResult = new FhirResult(bundleElement);
            MethodInfo method = typeof(FhirResult).GetMethod("GetResultToSerialize", BindingFlags.NonPublic | BindingFlags.Instance);
            object result = method.Invoke(fhirResult, Array.Empty<object>());

            // The whole point of the Ignixa path: FhirResult must hand the formatters the raw carrier, not a
            // hollow ToPoco() Bundle. Lock that in before exercising the formatters.
            Assert.IsType<IgnixaRawBundle>(result);
            return result;
        }

        private static async Task<string> WriteJson(object obj, string query)
        {
            var formatter = new IgnixaFhirJsonOutputFormatter(
                new IgnixaJsonSerializer(),
                new FhirJsonSerializer(),
                ModelInfoProvider.Instance,
                new BundleSerializer(),
                new IgnixaBundleSerializer(),
                Deserializers.ResourceDeserializer);

            using var body = new MemoryStream();
            var httpContext = new DefaultHttpContext();
            httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
            httpContext.Response.Body = body;
            if (query != null)
            {
                httpContext.Request.QueryString = new QueryString(query);
            }

            using var writer = new StringWriter();
            var writeContext = new OutputFormatterWriteContext(
                httpContext,
                (_, _) => writer,
                obj.GetType(),
                obj);

            await formatter.WriteResponseBodyAsync(writeContext, Encoding.UTF8);

            body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(body);
            return await reader.ReadToEndAsync();
        }

        private static async Task<string> WriteXml(object obj)
        {
            var formatter = new FhirXmlOutputFormatter(
                new FhirXmlSerializer(),
                Deserializers.ResourceDeserializer,
                ModelInfoProvider.Instance,
                new IgnixaJsonSerializer(),
                new IgnixaBundleSerializer());

            using var writer = new StringWriter(new StringBuilder());
            using var body = new MemoryStream();
            var httpContext = new DefaultHttpContext();
            httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
            httpContext.Response.Body = body;

            var writeContext = new OutputFormatterWriteContext(
                httpContext,
                (_, _) => writer,
                obj.GetType(),
                obj);

            await formatter.WriteResponseBodyAsync(writeContext, Encoding.UTF8);

            return writer.ToString();
        }
    }
}
