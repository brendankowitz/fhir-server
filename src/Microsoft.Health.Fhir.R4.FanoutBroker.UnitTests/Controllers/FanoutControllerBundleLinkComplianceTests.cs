// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Controllers;
using Microsoft.Health.Fhir.ValueSets;
using NSubstitute;
using Xunit;

using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.R4.FanoutBroker.UnitTests.Controllers
{
    /// <summary>
    /// Integration tests specifically validating FHIR R4 Bundle.link compliance requirements.
    /// These tests ensure that the Bundle.link generation meets FHIR R4 specification standards.
    /// </summary>
    public class FanoutControllerBundleLinkComplianceTests
    {
        private readonly ISearchService _mockSearchService;
        private readonly IConformanceProvider _mockConformanceProvider;
        private readonly IResourceDeserializer _mockResourceDeserializer;
        private readonly ILogger<FanoutController> _mockLogger;
        private readonly FanoutController _controller;

        public FanoutControllerBundleLinkComplianceTests()
        {
            _mockSearchService = Substitute.For<ISearchService>();
            _mockConformanceProvider = Substitute.For<IConformanceProvider>();
            _mockResourceDeserializer = Substitute.For<IResourceDeserializer>();
            _mockLogger = Substitute.For<ILogger<FanoutController>>();

            _controller = new FanoutController(
                _mockSearchService,
                _mockConformanceProvider,
                _mockResourceDeserializer,
                _mockLogger);
        }

        [Fact]
        public async Task SearchResultBundle_MustHaveSelfLink_FHIRR4Compliance()
        {
            // FHIR R4 Requirement: All search result bundles MUST have a self link
            // Reference: http://hl7.org/fhir/R4/http.html#search

            // Arrange
            SetupHttpContext("https", "fhir.example.com", "/Patient", "?name=John");
            var searchResult = CreateSearchResultWithResults();
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert - FHIR R4 Compliance Validation
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            // Bundle MUST have a link collection
            Assert.NotNull(bundle.Link);
            Assert.NotEmpty(bundle.Link);

            // Bundle MUST have a self link
            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);

            // Self link MUST have a valid URL
            Assert.NotNull(selfLink.Url);
            Assert.NotEmpty(selfLink.Url);

            // Self link URL MUST be a valid URI
            Assert.True(Uri.IsWellFormedUriString(selfLink.Url, UriKind.Absolute));

            // Self link MUST represent the exact search that was performed
            Assert.Contains("https://fhir.example.com/Patient", selfLink.Url);
            Assert.Contains("name=John", selfLink.Url);
        }

        [Fact]
        public async Task BundleLinks_MustUseIANALinkRelationTypes_FHIRR4Compliance()
        {
            // FHIR R4 Requirement: Bundle links must use IANA link relation types
            // Reference: https://hl7.org/fhir/R4/bundle.html

            // Arrange
            SetupHttpContext("https", "fhir.example.com", "/Patient", "?_count=10");
            var searchResult = CreateSearchResultWithPagination();
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert - FHIR R4 Compliance Validation
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            // Verify all link relations are valid IANA types
            Assert.All(bundle.Link, link =>
            {
                Assert.NotNull(link.Relation);
                Assert.NotEmpty(link.Relation);

                // Must be one of the standard IANA link relation types used in FHIR
                var validRelations = new[] { "self", "next", "prev", "previous", "first", "last" };
                Assert.Contains(link.Relation, validRelations);
            });

            // Verify specific required relations
            Assert.Contains(bundle.Link, l => l.Relation == "self");
            Assert.Contains(bundle.Link, l => l.Relation == "next");
        }

        [Fact]
        public async Task SelfLink_MustIncludeAllSearchParameters_FHIRR4Compliance()
        {
            // FHIR R4 Requirement: Self link must include all search parameters that were used
            // Reference: http://hl7.org/fhir/R4/http.html#search

            // Arrange
            var complexQueryString = "?name=John&family=Doe&birthdate=ge1980-01-01&active=true&_count=20&_include=Patient:organization";
            SetupHttpContext("https", "fhir.example.com", "/Patient", complexQueryString);

            var searchResult = CreateSearchResultWithResults();
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert - FHIR R4 Compliance Validation
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);

            // Self link MUST include all original search parameters
            Assert.Contains("name=John", selfLink.Url);
            Assert.Contains("family=Doe", selfLink.Url);
            Assert.Contains("birthdate=ge1980-01-01", selfLink.Url);
            Assert.Contains("active=true", selfLink.Url);
            Assert.Contains("_count=20", selfLink.Url);
            Assert.Contains("_include=Patient:organization", selfLink.Url);
        }

        [Fact]
        public async Task NextLink_MustBeValidGETRequest_FHIRR4Compliance()
        {
            // FHIR R4 Requirement: Next link must be a valid GET request
            // Reference: http://hl7.org/fhir/R4/http.html#search

            // Arrange
            SetupHttpContext("https", "fhir.example.com", "/Patient", "?name=John&_count=10");
            var searchResult = CreateSearchResultWithPagination();
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert - FHIR R4 Compliance Validation
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            var nextLink = bundle.Link.FirstOrDefault(l => l.Relation == "next");
            Assert.NotNull(nextLink);

            // Next link URL MUST be a valid URI
            Assert.True(Uri.IsWellFormedUriString(nextLink.Url, UriKind.Absolute));

            // Next link MUST preserve original search parameters
            Assert.Contains("name=John", nextLink.Url);
            Assert.Contains("_count=10", nextLink.Url);

            // Next link MUST include continuation token
            Assert.Contains("ct=", nextLink.Url);

            // Next link MUST be a GET request (no method restriction in URL, but should be navigable)
            var uri = new Uri(nextLink.Url);
            Assert.NotNull(uri.Query);
        }

        [Fact]
        public async Task EmptySearchResult_MustStillHaveSelfLink_FHIRR4Compliance()
        {
            // FHIR R4 Requirement: Even empty search results must have self link
            // Reference: http://hl7.org/fhir/R4/http.html#search

            // Arrange
            SetupHttpContext("https", "fhir.example.com", "/Patient", "?name=NonExistent");
            var searchResult = CreateEmptySearchResult();
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert - FHIR R4 Compliance Validation
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            // Empty result bundle MUST still have self link
            Assert.NotNull(bundle.Link);
            Assert.NotEmpty(bundle.Link);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);
            Assert.Contains("name=NonExistent", selfLink.Url);

            // Empty result bundle MUST NOT have next link
            var nextLink = bundle.Link.FirstOrDefault(l => l.Relation == "next");
            Assert.Null(nextLink);

            // Bundle should have zero total count
            Assert.Equal(0, bundle.Total);
            Assert.Empty(bundle.Entry);
        }

        [Fact]
        public async Task SystemLevelSearch_MustHaveSelfLink_FHIRR4Compliance()
        {
            // FHIR R4 Requirement: System-level searches must also have self links
            // Reference: http://hl7.org/fhir/R4/http.html#search

            // Arrange
            SetupHttpContext("https", "fhir.example.com", "/", "?_type=Patient,Observation&_lastUpdated=ge2023-01-01");
            var searchResult = CreateSearchResultWithResults();
            _mockSearchService.SearchAsync(null, Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.SystemSearch(CancellationToken.None);

            // Assert - FHIR R4 Compliance Validation
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            // System search MUST have self link
            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);

            // Self link MUST include system-level parameters
            Assert.Contains("_type=Patient,Observation", selfLink.Url);
            Assert.Contains("_lastUpdated=ge2023-01-01", selfLink.Url);
        }

        [Fact]
        public async Task IncludesOperation_MustHaveSelfLink_FHIRR4Compliance()
        {
            // FHIR R4 Requirement: All operations returning Bundle must have self link
            // Reference: http://hl7.org/fhir/R4/http.html#search

            // Arrange
            SetupHttpContext("https", "fhir.example.com", "/Patient/$includes", "?_include=Patient:organization&_count=5");
            var searchResult = CreateSearchResultWithResults();
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>(), true)
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.SearchIncludes("Patient", CancellationToken.None);

            // Assert - FHIR R4 Compliance Validation
            var objectResult = Assert.IsAssignableFrom<ObjectResult>(actionResult);

            // For $includes operation, we'll accept any 2xx status code or null (which defaults to 200)
            if (objectResult.StatusCode.HasValue && (objectResult.StatusCode < 200 || objectResult.StatusCode >= 300))
            {
                var errorContent = objectResult.Value?.ToString();
                Assert.Fail($"Expected 2xx status, got {objectResult.StatusCode}. Content: {errorContent}");
            }

            var bundle = Assert.IsType<Bundle>(objectResult.Value);

            // $includes operation MUST have self link
            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);

            // Self link MUST reflect the $includes operation path
            Assert.Contains("/Patient/$includes", selfLink.Url);
            Assert.Contains("_include=Patient:organization", selfLink.Url);
            Assert.Contains("_count=5", selfLink.Url);
        }

        [Theory]
        [InlineData("special+chars", "ct=special%2Bchars")]
        [InlineData("has=equals&and&ampersands", "ct=has%3Dequals%26and%26ampersands")]
        [InlineData("spaces and more", "ct=spaces%20and%20more")]
        [InlineData("unicode-ñoñó", "ct=unicode-%C3%B1o%C3%B1%C3%B3")]
        public async Task NextLink_MustProperlyEncodeContinuationToken_FHIRR4Compliance(
            string continuationToken, string expectedEncoding)
        {
            // FHIR R4 Requirement: URLs must be properly encoded
            // Reference: https://tools.ietf.org/html/rfc3986

            // Arrange
            SetupHttpContext("https", "fhir.example.com", "/Patient", "?name=John");
            var searchResult = CreateSearchResultWithPagination(continuationToken);
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert - FHIR R4 Compliance Validation
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            var nextLink = bundle.Link.FirstOrDefault(l => l.Relation == "next");
            Assert.NotNull(nextLink);

            // Continuation token MUST be properly URL encoded
            Assert.Contains(expectedEncoding, nextLink.Url);

            // Next link MUST still be a valid URI after encoding
            Assert.True(Uri.IsWellFormedUriString(nextLink.Url, UriKind.Absolute));
        }

        private void SetupHttpContext(string scheme, string host, string path, string queryString)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = scheme;
            httpContext.Request.Host = new HostString(host);
            httpContext.Request.Path = path;
            httpContext.Request.QueryString = new QueryString(queryString);

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            };
        }

        private SearchResult CreateSearchResultWithResults()
        {
            var patient = new Patient
            {
                Id = "test-patient-123",
                Name = new List<HumanName>
                {
                    new HumanName { Family = "Doe", Given = new[] { "John" } },
                },
            };

            var resourceElement = patient.ToResourceElement();
            var wrapper = new ResourceWrapper(
                resourceId: patient.Id,
                versionId: "1",
                resourceTypeName: "Patient",
                rawResource: new RawResource("{}", FhirResourceFormat.Json, isMetaSet: false),
                request: null,
                lastModified: DateTimeOffset.UtcNow,
                deleted: false,
                searchIndices: null,
                compartmentIndices: null,
                lastModifiedClaims: null);

            _mockResourceDeserializer.Deserialize(wrapper).Returns(resourceElement);

            return new SearchResult(
                results: new List<SearchResultEntry> { new SearchResultEntry(wrapper) },
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>())
            {
                TotalCount = 1,
                SourceServer = "https://source.fhir.com/fhir",
            };
        }

        private SearchResult CreateSearchResultWithPagination(string continuationToken = "test-token")
        {
            var searchResult = CreateSearchResultWithResults();
            return new SearchResult(
                results: searchResult.Results,
                continuationToken: continuationToken,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>())
            {
                TotalCount = 100, // More results available
                SourceServer = searchResult.SourceServer,
            };
        }

        private SearchResult CreateEmptySearchResult()
        {
            return new SearchResult(
                results: new List<SearchResultEntry>(),
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>())
            {
                TotalCount = 0,
                SourceServer = "https://source.fhir.com/fhir",
            };
        }
    }
}
