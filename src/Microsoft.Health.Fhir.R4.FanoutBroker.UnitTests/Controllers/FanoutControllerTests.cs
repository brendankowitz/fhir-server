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
    public class FanoutControllerTests
    {
        private readonly ISearchService _mockSearchService;
        private readonly IConformanceProvider _mockConformanceProvider;
        private readonly IResourceDeserializer _mockResourceDeserializer;
        private readonly ILogger<FanoutController> _mockLogger;
        private readonly FanoutController _controller;

        public FanoutControllerTests()
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

            // Setup HttpContext for URL generation
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("fhir-fanout.example.com");
            httpContext.Request.Path = "/Patient";
            httpContext.Request.QueryString = new QueryString("?name=John&active=true");

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            };
        }

        [Fact]
        public async Task SystemSearch_WithValidResults_CreatesBundleWithSelfLink()
        {
            // Arrange
            var searchResult = CreateMockSearchResult(withResults: true, withContinuation: false);
            _mockSearchService.SearchAsync(null, Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.SystemSearch(CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            // Verify Bundle.link collection exists and contains self link
            Assert.NotNull(bundle.Link);
            Assert.NotEmpty(bundle.Link);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);
            Assert.NotNull(selfLink.Url);
            Assert.Contains("https://fhir-fanout.example.com/Patient", selfLink.Url);
            Assert.Contains("name=John", selfLink.Url);
            Assert.Contains("active=true", selfLink.Url);
        }

        [Fact]
        public async Task ResourceSearch_WithValidResults_CreatesBundleWithSelfLink()
        {
            // Arrange
            var searchResult = CreateMockSearchResult(withResults: true, withContinuation: false);
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            // Verify Bundle.link collection exists and contains self link
            Assert.NotNull(bundle.Link);
            Assert.NotEmpty(bundle.Link);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);
            Assert.NotNull(selfLink.Url);
            Assert.Contains("https://fhir-fanout.example.com/Patient", selfLink.Url);
        }

        [Fact]
        public async Task SystemSearch_WithPagination_CreatesBundleWithSelfAndNextLinks()
        {
            // Arrange
            var searchResult = CreateMockSearchResult(withResults: true, withContinuation: true);
            _mockSearchService.SearchAsync(null, Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.SystemSearch(CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            // Verify Bundle.link collection exists and contains both self and next links
            Assert.NotNull(bundle.Link);
            Assert.Equal(2, bundle.Link.Count);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);
            Assert.NotNull(selfLink.Url);
            Assert.Contains("https://fhir-fanout.example.com/Patient", selfLink.Url);

            var nextLink = bundle.Link.FirstOrDefault(l => l.Relation == "next");
            Assert.NotNull(nextLink);
            Assert.NotNull(nextLink.Url);
            Assert.Contains("ct=", nextLink.Url);
            Assert.Contains("test-continuation-token", nextLink.Url);
        }

        [Fact]
        public async Task ResourceSearch_WithPagination_CreatesBundleWithSelfAndNextLinks()
        {
            // Arrange
            var searchResult = CreateMockSearchResult(withResults: true, withContinuation: true);
            _mockSearchService.SearchAsync("Observation", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Observation", CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            // Verify Bundle.link collection exists and contains both self and next links
            Assert.NotNull(bundle.Link);
            Assert.Equal(2, bundle.Link.Count);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);
            Assert.Contains("self", selfLink.Relation);

            var nextLink = bundle.Link.FirstOrDefault(l => l.Relation == "next");
            Assert.NotNull(nextLink);
            Assert.Contains("next", nextLink.Relation);
            Assert.Contains("ct=", nextLink.Url);
        }

        [Fact]
        public async Task SearchIncludes_WithValidResults_CreatesBundleWithSelfLink()
        {
            // Arrange
            var searchResult = CreateMockSearchResult(withResults: true, withContinuation: false);
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>(), true)
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.SearchIncludes("Patient", CancellationToken.None);

            // Assert
            var objectResult = Assert.IsAssignableFrom<ObjectResult>(actionResult);

            // For $includes operation, we'll accept any 2xx status code or null (which defaults to 200)
            if (objectResult.StatusCode.HasValue && (objectResult.StatusCode < 200 || objectResult.StatusCode >= 300))
            {
                var errorContent = objectResult.Value?.ToString();
                Assert.Fail($"Expected 2xx status, got {objectResult.StatusCode}. Content: {errorContent}");
            }

            var bundle = Assert.IsType<Bundle>(objectResult.Value);

            // Verify Bundle.link collection exists and contains self link
            Assert.NotNull(bundle.Link);
            Assert.NotEmpty(bundle.Link);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);
            Assert.NotNull(selfLink.Url);
            Assert.Contains("https://fhir-fanout.example.com/Patient", selfLink.Url);
        }

        [Fact]
        public async Task SystemSearch_WithEmptyResults_CreatesBundleWithSelfLinkOnly()
        {
            // Arrange
            var searchResult = CreateMockSearchResult(withResults: false, withContinuation: false);
            _mockSearchService.SearchAsync(null, Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.SystemSearch(CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            // Verify Bundle.link collection exists and contains only self link
            Assert.NotNull(bundle.Link);
            Assert.Single(bundle.Link);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);
            Assert.NotNull(selfLink.Url);

            // Should not have next link for empty results
            var nextLink = bundle.Link.FirstOrDefault(l => l.Relation == "next");
            Assert.Null(nextLink);
        }

        [Fact]
        public async Task ResourceSearch_WithSpecialCharactersInContinuationToken_CreatesProperlyEncodedNextLink()
        {
            // Arrange
            var searchResult = CreateMockSearchResult(withResults: true, withContinuation: true, continuationToken: "token+with=special&chars");
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            var nextLink = bundle.Link.FirstOrDefault(l => l.Relation == "next");
            Assert.NotNull(nextLink);

            // Verify that special characters in continuation token are properly URL encoded
            Assert.Contains("ct=token%2Bwith%3Dspecial%26chars", nextLink.Url);
        }

        [Theory]
        [InlineData("https", "api.fhir.com", "/Patient", "?name=John", "https://api.fhir.com/Patient?name=John")]
        [InlineData("http", "localhost:8080", "/Observation", "?status=final&_count=10", "http://localhost:8080/Observation?status=final&_count=10")]
        [InlineData("https", "fhir.example.org", "/", "?_type=Patient,Observation", "https://fhir.example.org/?_type=Patient,Observation")]
        public async Task SystemSearch_WithVariousUrlFormats_GeneratesCorrectSelfLink(
            string scheme, string host, string path, string queryString, string expectedSelfUrl)
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = scheme;
            httpContext.Request.Host = new HostString(host);
            httpContext.Request.Path = path;
            httpContext.Request.QueryString = new QueryString(queryString);

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            };

            var searchResult = CreateMockSearchResult(withResults: true, withContinuation: false);
            _mockSearchService.SearchAsync(null, Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.SystemSearch(CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);
            Assert.Equal(expectedSelfUrl, selfLink.Url);
        }

        [Fact]
        public async Task ResourceSearch_WhenRequestHasNoQueryString_GeneratesSelfLinkWithoutQueryParameters()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("fhir.example.com");
            httpContext.Request.Path = "/Patient";
            httpContext.Request.QueryString = QueryString.Empty;

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            };

            var searchResult = CreateMockSearchResult(withResults: true, withContinuation: false);
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            var selfLink = bundle.Link.FirstOrDefault(l => l.Relation == "self");
            Assert.NotNull(selfLink);
            Assert.Equal("https://fhir.example.com/Patient", selfLink.Url);
        }

        [Fact]
        public async Task ResourceSearch_WhenContinuationTokenAndExistingQueryParams_GeneratesNextLinkWithProperSeparator()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("fhir.example.com");
            httpContext.Request.Path = "/Patient";
            httpContext.Request.QueryString = new QueryString("?name=John&active=true");

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            };

            var searchResult = CreateMockSearchResult(withResults: true, withContinuation: true);
            _mockSearchService.SearchAsync("Patient", Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<CancellationToken>())
                .Returns(searchResult);

            // Act
            var actionResult = await _controller.ResourceSearch("Patient", CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var bundle = Assert.IsType<Bundle>(okResult.Value);

            var nextLink = bundle.Link.FirstOrDefault(l => l.Relation == "next");
            Assert.NotNull(nextLink);

            // Should use '&' separator when query parameters already exist
            Assert.Contains("name=John&active=true&ct=", nextLink.Url);
        }

        private SearchResult CreateMockSearchResult(bool withResults, bool withContinuation, string continuationToken = "test-continuation-token")
        {
            var results = new List<SearchResultEntry>();

            if (withResults)
            {
                // Create mock patient resource
                var mockPatient = CreateMockPatient();
                var mockResourceWrapper = CreateMockResourceWrapper(mockPatient);

                results.Add(new SearchResultEntry(mockResourceWrapper));
            }

            return new SearchResult(
                results: results,
                continuationToken: withContinuation ? continuationToken : null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>())
            {
                TotalCount = withResults ? 1 : 0,
                SourceServer = "https://source.fhir.com/fhir",
            };
        }

        private Patient CreateMockPatient()
        {
            return new Patient
            {
                Id = "test-patient-123",
                Name = new List<HumanName>
                {
                    new HumanName
                    {
                        Family = "Doe",
                        Given = new[] { "John" },
                    },
                },
                Active = true,
            };
        }

        private ResourceWrapper CreateMockResourceWrapper(Patient patient)
        {
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

            return wrapper;
        }
    }
}
