// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Hl7.Fhir.Model;
using Ignixa.Serialization.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources.Bundle;
using Microsoft.Health.Fhir.Core.Features.Routing;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Shared.Core.Features.Search;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class IgnixaBundleFactoryTests
    {
        private readonly IUrlResolver _urlResolver = Substitute.For<IUrlResolver>();
        private readonly RequestContextAccessor<IFhirRequestContext> _fhirRequestContextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
        private readonly IIgnixaSchemaContext _schemaContext = new IgnixaSchemaContext(ModelInfoProvider.Instance);
        private readonly BundleFactory _bundleFactory;
        private readonly IgnixaBundleFactory _ignixaBundleFactory;
        private readonly IgnixaBundleSerializer _ignixaBundleSerializer = new IgnixaBundleSerializer();

        private const string _resourceUrlFormat = "http://resource/{0}";
        private static readonly string _correlationId = Guid.NewGuid().ToString();
        private static readonly Uri _selfUrl = new Uri("http://self/");
        private static readonly IReadOnlyList<Tuple<string, string>> _unsupportedSearchParameters = Array.Empty<Tuple<string, string>>();

        public IgnixaBundleFactoryTests()
        {
            _bundleFactory = new BundleFactory(
                _urlResolver,
                _fhirRequestContextAccessor,
                NullLogger<BundleFactory>.Instance);

            _ignixaBundleFactory = new IgnixaBundleFactory(
                _urlResolver,
                _fhirRequestContextAccessor,
                _schemaContext,
                NullLogger<IgnixaBundleFactory>.Instance);

            IFhirRequestContext fhirRequestContext = Substitute.For<IFhirRequestContext>();
            fhirRequestContext.CorrelationId.Returns(_correlationId);
            fhirRequestContext.BundleIssues.Returns(new List<OperationOutcomeIssue>());

            _fhirRequestContextAccessor.RequestContext.Returns(fhirRequestContext);
        }

        [Fact]
        public void GivenAnEmptySearchResult_WhenCreateSearchBundle_ThenIgnixaOutputMatchesFirelyOutput()
        {
            _urlResolver.ResolveRouteUrl(_unsupportedSearchParameters).Returns(_selfUrl);

            var searchResult = new SearchResult(Array.Empty<SearchResultEntry>(), null, null, _unsupportedSearchParameters);

            ResourceElement firelyActual = _bundleFactory.CreateSearchBundle(searchResult);
            ResourceElement ignixaActual = _ignixaBundleFactory.CreateSearchBundle(searchResult);

            Assert.Equal("Bundle", ignixaActual.InstanceType);

            IgnixaRawBundle rawBundle = ignixaActual.GetIgnixaRawBundle();
            Assert.NotNull(rawBundle);
            Assert.Empty(rawBundle.Entries);

            Assert.Equal(firelyActual.Id, rawBundle.Skeleton.Id);
            Assert.Equal(BundleJsonNode.BundleType.Searchset, rawBundle.Skeleton.Type);
            Assert.Equal(firelyActual.ToPoco<Bundle>().Total, rawBundle.Skeleton.Total);
            Assert.Equal(
                firelyActual.Scalar<string>("Bundle.link.where(relation='self').url"),
                rawBundle.Skeleton.Link.Single(l => l.Relation == "self").Url);
        }

        [Fact]
        public void GivenASearchResult_WhenCreateSearchBundle_ThenIgnixaEntriesMatchFirelyEntries()
        {
            _urlResolver.ResolveResourceWrapperUrl(Arg.Any<ResourceWrapper>()).Returns(x => new Uri(string.Format(_resourceUrlFormat, x.ArgAt<ResourceWrapper>(0).ResourceId)));
            _urlResolver.ResolveRouteUrl(_unsupportedSearchParameters).Returns(_selfUrl);

            ResourceElement observation1 = Samples.GetDefaultObservation().UpdateId("123");
            ResourceElement observation2 = Samples.GetDefaultObservation().UpdateId("abc");

            var resourceWrappers = new[]
            {
                new SearchResultEntry(CreateResourceWrapper(observation1, HttpMethod.Post)),
                new SearchResultEntry(CreateResourceWrapper(observation2, HttpMethod.Post)),
            };

            var searchResult = new SearchResult(resourceWrappers, continuationToken: null, sortOrder: null, unsupportedSearchParameters: _unsupportedSearchParameters);

            ResourceElement firelyActual = _bundleFactory.CreateSearchBundle(searchResult);
            ResourceElement ignixaActual = _ignixaBundleFactory.CreateSearchBundle(searchResult);

            Bundle firelyBundle = firelyActual.ToPoco<Bundle>();
            IgnixaRawBundle rawBundle = ignixaActual.GetIgnixaRawBundle();

            Assert.Equal(firelyBundle.Entry.Count, rawBundle.Entries.Count);

            for (int i = 0; i < firelyBundle.Entry.Count; i++)
            {
                Bundle.EntryComponent firelyEntry = firelyBundle.Entry[i];
                IgnixaRawBundleEntry ignixaEntry = rawBundle.Entries[i];

                Assert.Equal(firelyEntry.FullUrl, ignixaEntry.Metadata.FullUrl);
                Assert.Equal(Bundle.SearchEntryMode.Match.ToString().ToLowerInvariant(), ignixaEntry.Metadata.Search.Mode);
                Assert.NotNull(ignixaEntry.RawResource);
            }

            using JsonDocument serialized = SerializeToJsonDocument(rawBundle);
            JsonElement entries = serialized.RootElement.GetProperty("entry");

            Assert.Equal("123", entries[0].GetProperty("resource").GetProperty("id").GetString());
            Assert.Equal("abc", entries[1].GetProperty("resource").GetProperty("id").GetString());
        }

        [Theory]
        [InlineData("123", "1", "POST", "201 Created")]
        [InlineData("123", "1", "PUT", "201 Created")]
        [InlineData("123", "2", "PUT", "200 OK")]

        // Bundle.HTTPVerb.PATCH is part of Firely's version-agnostic Bundle model (Hl7.Fhir.Base), so it
        // round-trips even under STU3: both factories remap it to PUT (Firely via the
        // "httpVerb == Bundle.HTTPVerb.PATCH" check in BundleFactory, Ignixa via TryResolveVerb's STU3-only
        // "PATCH" case), landing on the same "200 OK" for a non-initial version. This is the only place that
        // exercises the STU3 PATCH->PUT branch in IgnixaBundleFactory.TryResolveVerb.
        [InlineData("123", "2", "PATCH", "200 OK")]
        [InlineData("123", "2", "DELETE", "204 NoContent")]
        public void GivenAHistoryResultWithDifferentStatuses_WhenCreateHistoryBundle_ThenIgnixaMatchesFirely(string id, string version, string method, string statusString)
        {
            _urlResolver.ResolveResourceWrapperUrl(Arg.Any<ResourceWrapper>()).Returns(x => new Uri(string.Format(_resourceUrlFormat, x.ArgAt<ResourceWrapper>(0).ResourceId)));
            _urlResolver.ResolveRouteUrl(_unsupportedSearchParameters).Returns(_selfUrl);

            ResourceElement observation1 = Samples.GetDefaultObservation().UpdateId(id).UpdateVersion(version);

            var resourceWrappers = new[]
            {
                new SearchResultEntry(CreateResourceWrapper(observation1, new HttpMethod(method))),
            };

            var searchResult = new SearchResult(resourceWrappers, continuationToken: null, sortOrder: null, unsupportedSearchParameters: _unsupportedSearchParameters);

            ResourceElement firelyActual = _bundleFactory.CreateHistoryBundle(searchResult);
            ResourceElement ignixaActual = _ignixaBundleFactory.CreateHistoryBundle(searchResult);

            Bundle.EntryComponent firelyEntry = firelyActual.ToPoco<Bundle>().Entry[0];
            IgnixaRawBundleEntry ignixaEntry = ignixaActual.GetIgnixaRawBundle().Entries[0];

            Assert.Equal(statusString, firelyEntry.Response.Status);
            Assert.Equal(statusString, ignixaEntry.Metadata.Response.Status);
            Assert.Equal(firelyEntry.Request.Method.ToString().ToUpperInvariant(), ignixaEntry.Metadata.Request.Method);
            Assert.Equal(firelyEntry.Request.Url, ignixaEntry.Metadata.Request.Url);
        }

        [Theory]
        [InlineData(true, "204 NoContent")]
        [InlineData(false, "200 OK")]
        public void GivenAHistoryResultWithImportPseudoVerb_WhenCreateHistoryBundle_ThenIsDeletedControlsDeleteVsPut(bool isDeleted, string expectedStatus)
        {
            _urlResolver.ResolveResourceWrapperUrl(Arg.Any<ResourceWrapper>()).Returns(x => new Uri(string.Format(_resourceUrlFormat, x.ArgAt<ResourceWrapper>(0).ResourceId)));
            _urlResolver.ResolveRouteUrl(_unsupportedSearchParameters).Returns(_selfUrl);

            ResourceElement observation = Samples.GetDefaultObservation().UpdateId("del-123").UpdateVersion("2");
            ResourceWrapper wrapper = new ResourceWrapper(
                observation,
                new RawResource(new Hl7.Fhir.Serialization.FhirJsonSerializer().SerializeToString(observation.ToPoco<Observation>()), FhirResourceFormat.Json, isMetaSet: false),
                new ResourceRequest("Import", url: null),
                isDeleted,
                null,
                null,
                null);

            var searchResult = new SearchResult(new[] { new SearchResultEntry(wrapper) }, continuationToken: null, sortOrder: null, unsupportedSearchParameters: _unsupportedSearchParameters);

            ResourceElement firelyActual = _bundleFactory.CreateHistoryBundle(searchResult);
            ResourceElement ignixaActual = _ignixaBundleFactory.CreateHistoryBundle(searchResult);

            Bundle.EntryComponent firelyEntry = firelyActual.ToPoco<Bundle>().Entry[0];
            IgnixaRawBundleEntry ignixaEntry = ignixaActual.GetIgnixaRawBundle().Entries[0];

            string expectedVerb = isDeleted ? "DELETE" : "PUT";

            Assert.Equal(expectedVerb, firelyEntry.Request.Method.ToString().ToUpperInvariant());
            Assert.Equal(expectedVerb, ignixaEntry.Metadata.Request.Method);
            Assert.Equal(expectedStatus, firelyEntry.Response.Status);
            Assert.Equal(expectedStatus, ignixaEntry.Metadata.Response.Status);
        }

        [Fact]
        public void GivenBundleLevelLinkProblem_WhenCreateSearchBundle_ThenOperationOutcomeEntryIsAdded()
        {
            _urlResolver.ResolveRouteUrl(_unsupportedSearchParameters).Returns(x => throw new UriFormatException("bad self link"));

            var searchResult = new SearchResult(Array.Empty<SearchResultEntry>(), null, null, _unsupportedSearchParameters);

            ResourceElement ignixaActual = _ignixaBundleFactory.CreateSearchBundle(searchResult);

            IgnixaRawBundle rawBundle = ignixaActual.GetIgnixaRawBundle();
            Assert.Single(rawBundle.Entries);

            IgnixaRawBundleEntry outcomeEntry = rawBundle.Entries[0];
            Assert.Equal("outcome", outcomeEntry.Metadata.Search.Mode);
            Assert.NotNull(outcomeEntry.ResourceNode);
            Assert.Equal("OperationOutcome", outcomeEntry.ResourceNode.ResourceType);

            var operationOutcome = (OperationOutcomeJsonNode)outcomeEntry.ResourceNode;
            Assert.Single(operationOutcome.Issue);
            Assert.Equal(OperationOutcomeJsonNode.IssueSeverity.Warning, operationOutcome.Issue[0].Severity);
            Assert.Equal(OperationOutcomeJsonNode.IssueType.NotSupported, operationOutcome.Issue[0].Code);
        }

        [Fact]
        public void GivenAnIssueWithEmptyDetailsCodesAndNoDetailsText_WhenCreateSearchBundle_ThenIgnixaOmitsDetailsMatchingFirely()
        {
            _urlResolver.ResolveRouteUrl(_unsupportedSearchParameters).Returns(_selfUrl);

            // An empty-but-non-null DetailsCodes combined with a null DetailsText must produce NO Details
            // node at all -- mirrors CommonModelExtensions.ToPoco()'s "coding.Count != 0 || DetailsText != null"
            // condition, not a blind null-check on DetailsCodes.
            var issue = new OperationOutcomeIssue(
                OperationOutcomeConstants.IssueSeverity.Warning,
                OperationOutcomeConstants.IssueType.Informational,
                detailsCodes: new CodableConceptInfo());

            var searchResult = new SearchResult(Array.Empty<SearchResultEntry>(), null, null, _unsupportedSearchParameters, new[] { issue });

            ResourceElement firelyActual = _bundleFactory.CreateSearchBundle(searchResult);
            ResourceElement ignixaActual = _ignixaBundleFactory.CreateSearchBundle(searchResult);

            var firelyOutcome = (OperationOutcome)firelyActual.ToPoco<Bundle>().Entry[0].Resource;
            Assert.Null(firelyOutcome.Issue[0].Details);

            var ignixaOutcome = (OperationOutcomeJsonNode)ignixaActual.GetIgnixaRawBundle().Entries[0].ResourceNode;
            Assert.Null(ignixaOutcome.Issue[0].Details);
        }

        [Fact]
        public void GivenAnIssueWithExpression_WhenCreateSearchBundle_ThenIgnixaLocationMatchesFirelyLocation()
        {
            _urlResolver.ResolveRouteUrl(_unsupportedSearchParameters).Returns(_selfUrl);

            // Location is auto-derived from Expression (OperationOutcomeIssue's constructor). This locks
            // in that IgnixaBundleFactory.ToIgnixaIssueComponent reproduces that derivation exactly, since
            // Ignixa's IssueComponent has no typed Location property to fall back on.
            var issue = new OperationOutcomeIssue(
                OperationOutcomeConstants.IssueSeverity.Error,
                OperationOutcomeConstants.IssueType.Invalid,
                expression: new[] { "Patient.name[0].family", "Patient.birthDate" });

            var searchResult = new SearchResult(Array.Empty<SearchResultEntry>(), null, null, _unsupportedSearchParameters, new[] { issue });

            ResourceElement firelyActual = _bundleFactory.CreateSearchBundle(searchResult);
            ResourceElement ignixaActual = _ignixaBundleFactory.CreateSearchBundle(searchResult);

            var firelyOutcome = (OperationOutcome)firelyActual.ToPoco<Bundle>().Entry[0].Resource;
#pragma warning disable CS0618 // Type or member is obsolete
            string[] expectedLocation = firelyOutcome.Issue[0].Location.ToArray();
#pragma warning restore CS0618

            Assert.NotEmpty(expectedLocation);

            IgnixaRawBundle rawBundle = ignixaActual.GetIgnixaRawBundle();
            using JsonDocument serialized = SerializeToJsonDocument(rawBundle);
            JsonElement locationElement = serialized.RootElement
                .GetProperty("entry")[0]
                .GetProperty("resource")
                .GetProperty("issue")[0]
                .GetProperty("location");

            string[] actualLocation = locationElement.EnumerateArray().Select(e => e.GetString()).ToArray();

            Assert.Equal(expectedLocation, actualLocation);
        }

        [Fact]
        public void GivenAnIgnixaSearchBundleWithEntries_WhenToPocoIsCalled_ThenPocoEntriesAreHollowButRawBundleEntriesAreReal()
        {
            _urlResolver.ResolveResourceWrapperUrl(Arg.Any<ResourceWrapper>()).Returns(x => new Uri(string.Format(_resourceUrlFormat, x.ArgAt<ResourceWrapper>(0).ResourceId)));
            _urlResolver.ResolveRouteUrl(_unsupportedSearchParameters).Returns(_selfUrl);

            ResourceElement observation1 = Samples.GetDefaultObservation().UpdateId("123");

            var resourceWrappers = new[]
            {
                new SearchResultEntry(CreateResourceWrapper(observation1, HttpMethod.Post)),
            };

            var searchResult = new SearchResult(resourceWrappers, continuationToken: null, sortOrder: null, unsupportedSearchParameters: _unsupportedSearchParameters);

            ResourceElement ignixaActual = _ignixaBundleFactory.CreateSearchBundle(searchResult);

            // The invariant this locks in: GetIgnixaRawBundle() sees the real entries, but ToPoco() --
            // which only ever sees the skeleton -- silently reports zero. Every consumer of an
            // Ignixa-native bundle MUST read it via GetIgnixaRawBundle(), never ToPoco().
            Assert.NotNull(ignixaActual.GetIgnixaRawBundle());
            Assert.True(ignixaActual.GetIgnixaRawBundle().Entries.Count > 0);
            Assert.Empty(ignixaActual.ToPoco<Bundle>().Entry);
        }

        private ResourceWrapper CreateResourceWrapper(ResourceElement resourceElement, HttpMethod httpMethod)
        {
            return new ResourceWrapper(
                resourceElement,
                new RawResource(new Hl7.Fhir.Serialization.FhirJsonSerializer().SerializeToString(resourceElement.ToPoco<Observation>()), FhirResourceFormat.Json, isMetaSet: false),
                new ResourceRequest(httpMethod, url: "http://test/Resource/resourceId"),
                false,
                null,
                null,
                null);
        }

        private JsonDocument SerializeToJsonDocument(IgnixaRawBundle bundle)
        {
            using var stream = new System.IO.MemoryStream();
            _ignixaBundleSerializer.Serialize(bundle, stream).GetAwaiter().GetResult();
            stream.Seek(0, System.IO.SeekOrigin.Begin);
            return JsonDocument.Parse(stream.ToArray());
        }
    }
}
