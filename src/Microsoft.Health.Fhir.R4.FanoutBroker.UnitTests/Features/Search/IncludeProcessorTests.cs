// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Models;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.FanoutBroker.UnitTests.Features.Search
{
    public class IncludeProcessorTests
    {
        private readonly IFhirServerOrchestrator _serverOrchestrator;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<IncludeProcessor> _logger;
        private readonly IncludeProcessor _includeProcessor;

        public IncludeProcessorTests()
        {
            _serverOrchestrator = Substitute.For<IFhirServerOrchestrator>();
            _configuration = Substitute.For<IOptions<FanoutBrokerConfiguration>>();
            _logger = Substitute.For<ILogger<IncludeProcessor>>();

            _configuration.Value.Returns(new FanoutBrokerConfiguration
            {
                FhirServers = new List<FhirServerEndpoint>
                {
                    new FhirServerEndpoint { Id = "server1", BaseUrl = "https://server1.com/fhir", IsEnabled = true },
                    new FhirServerEndpoint { Id = "server2", BaseUrl = "https://server2.com/fhir", IsEnabled = true },
                },
            });

            _includeProcessor = new IncludeProcessor(_serverOrchestrator, _configuration, _logger);
        }

        [Fact]
        public void HasIncludeParameters_WithIncludeParameter_ReturnsTrue()
        {
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_include", "Patient:organization"),
            };

            var result = _includeProcessor.HasIncludeParameters(queryParameters);
            Assert.True(result);
        }

        [Fact]
        public void HasIncludeParameters_WithRevIncludeParameter_ReturnsTrue()
        {
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_revinclude", "Observation:patient"),
            };

            var result = _includeProcessor.HasIncludeParameters(queryParameters);
            Assert.True(result);
        }

        [Fact]
        public void HasIncludeParameters_WithoutIncludeParameters_ReturnsFalse()
        {
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_sort", "_lastModified"),
            };

            var result = _includeProcessor.HasIncludeParameters(queryParameters);
            Assert.False(result);
        }

        [Fact]
        public void HasIncludeParameters_CaseInsensitive_ReturnsTrue()
        {
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_INCLUDE", "Patient:organization"),
                new Tuple<string, string>("_RevInclude", "Observation:patient"),
            };

            var result = _includeProcessor.HasIncludeParameters(queryParameters);
            Assert.True(result);
        }

        [Fact]
        public async System.Threading.Tasks.Task ProcessIncludesAsync_WithoutIncludeParameters_ReturnsOriginalResult()
        {
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
            };

            var originalResult = new SearchResult(
                results: new List<SearchResultEntry>
                {
                    CreateSearchResultEntry("Patient", "1"),
                },
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>());

            var result = await _includeProcessor.ProcessIncludesAsync(
                "Patient",
                queryParameters,
                originalResult,
                CancellationToken.None);

            Assert.Same(originalResult, result);
            Assert.Single(result.Results);
        }

        [Fact]
        public async System.Threading.Tasks.Task ProcessIncludesAsync_WithIncludeParameters_ProcessesIncludes()
        {
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_include", "Patient:organization"),
            };

            var originalResult = new SearchResult(
                results: new List<SearchResultEntry>
                {
                    CreateSearchResultEntry("Patient", "1"),
                },
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>());

            var server1Result = new ServerSearchResult
            {
                ServerId = "server1",
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org1"),
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>()),
            };

            var server2Result = new ServerSearchResult
            {
                ServerId = "server2",
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org2"),
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>()),
            };

            _serverOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>
            {
                new FhirServerEndpoint { Id = "server1", BaseUrl = "https://server1.com/fhir", IsEnabled = true },
                new FhirServerEndpoint { Id = "server2", BaseUrl = "https://server2.com/fhir", IsEnabled = true },
            });

            _serverOrchestrator.SearchAsync(Arg.Any<FhirServerEndpoint>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(server1Result, server2Result);

            var result = await _includeProcessor.ProcessIncludesAsync(
                "Patient",
                queryParameters,
                originalResult,
                CancellationToken.None);

            Assert.NotSame(originalResult, result);
            Assert.Equal(3, result.Results.Count());

            var resourceTypes = result.Results.Select(r => r.Resource.ResourceTypeName).ToList();
            Assert.Contains("Patient", resourceTypes);
            Assert.Contains("Organization", resourceTypes);
            Assert.Equal(2, resourceTypes.Count(rt => rt == "Organization"));
        }

        [Fact]
        public async System.Threading.Tasks.Task ProcessIncludesOperationAsync_ReturnsIncludedResourcesOnly()
        {
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("_include", "Patient:organization"),
                new Tuple<string, string>("_count", "50"),
            };

            var serverResult = new ServerSearchResult
            {
                ServerId = "server1",
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org1"),
                        CreateSearchResultEntry("Organization", "org2"),
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>()),
            };

            _serverOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>
            {
                new FhirServerEndpoint { Id = "server1", BaseUrl = "https://server1.com/fhir", IsEnabled = true },
            });

            _serverOrchestrator.SearchAsync(Arg.Any<FhirServerEndpoint>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(serverResult);

            var result = await _includeProcessor.ProcessIncludesOperationAsync(
                "Patient",
                queryParameters,
                CancellationToken.None);

            Assert.Equal(2, result.Results.Count());
            Assert.All(result.Results, r => Assert.Equal("Organization", r.Resource.ResourceTypeName));
        }

        [Fact]
        public async System.Threading.Tasks.Task ProcessIncludesAsync_HandlesDuplicateResources()
        {
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_include", "Patient:organization"),
            };

            var originalResult = new SearchResult(
                results: new List<SearchResultEntry>
                {
                    CreateSearchResultEntry("Patient", "1"),
                },
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>());

            var serverResult1 = new ServerSearchResult
            {
                ServerId = "server1",
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org1"),
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>()),
            };

            var serverResult2 = new ServerSearchResult
            {
                ServerId = "server2",
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org1"),
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>()),
            };

            _serverOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>
            {
                new FhirServerEndpoint { Id = "server1", BaseUrl = "https://server1.com/fhir", IsEnabled = true },
                new FhirServerEndpoint { Id = "server2", BaseUrl = "https://server2.com/fhir", IsEnabled = true },
            });

            _serverOrchestrator.SearchAsync(Arg.Any<FhirServerEndpoint>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(serverResult1, serverResult2);

            var result = await _includeProcessor.ProcessIncludesAsync(
                "Patient",
                queryParameters,
                originalResult,
                CancellationToken.None);

            Assert.Equal(2, result.Results.Count());

            var organizations = result.Results.Where(r => r.Resource.ResourceTypeName == "Organization").ToList();
            Assert.Single(organizations);
            Assert.Equal("org1", organizations.First().Resource.ResourceId);
        }

        private SearchResultEntry CreateSearchResultEntry(string resourceType, string id)
        {
            var wrapper = new Microsoft.Health.Fhir.Core.Features.Persistence.ResourceWrapper(
                resourceId: id,
                versionId: "1",
                resourceTypeName: resourceType,
                rawResource: new RawResource("{}", FhirResourceFormat.Json, isMetaSet: false),
                request: null,
                lastModified: DateTimeOffset.UtcNow,
                deleted: false,
                searchIndices: null,
                compartmentIndices: null,
                lastModifiedClaims: null);

            return new SearchResultEntry(wrapper, Microsoft.Health.Fhir.ValueSets.SearchEntryMode.Match);
        }
    }
}
