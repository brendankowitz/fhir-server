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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

            // Setup configuration
            _configuration.Value.Returns(new FanoutBrokerConfiguration
            {
                FhirServers = new List<FhirServerEndpoint>
                {
                    new FhirServerEndpoint { Id = "server1", BaseUrl = "https://server1.com/fhir", IsEnabled = true },
                    new FhirServerEndpoint { Id = "server2", BaseUrl = "https://server2.com/fhir", IsEnabled = true }
                }
            });

            _includeProcessor = new IncludeProcessor(_serverOrchestrator, _configuration, _logger);
        }

        [Fact]
        public void HasIncludeParameters_WithIncludeParameter_ReturnsTrue()
        {
            // Arrange
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_include", "Patient:organization")
            };

            // Act
            var result = _includeProcessor.HasIncludeParameters(queryParameters);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void HasIncludeParameters_WithRevIncludeParameter_ReturnsTrue()
        {
            // Arrange
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_revinclude", "Observation:patient")
            };

            // Act
            var result = _includeProcessor.HasIncludeParameters(queryParameters);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void HasIncludeParameters_WithoutIncludeParameters_ReturnsFalse()
        {
            // Arrange
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_sort", "_lastModified")
            };

            // Act
            var result = _includeProcessor.HasIncludeParameters(queryParameters);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void HasIncludeParameters_CaseInsensitive_ReturnsTrue()
        {
            // Arrange
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_INCLUDE", "Patient:organization"),
                new Tuple<string, string>("_RevInclude", "Observation:patient")
            };

            // Act
            var result = _includeProcessor.HasIncludeParameters(queryParameters);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task ProcessIncludesAsync_WithoutIncludeParameters_ReturnsOriginalResult()
        {
            // Arrange
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John")
            };

            var originalResult = new SearchResult(
                results: new List<SearchResultEntry>
                {
                    CreateSearchResultEntry("Patient", "1")
                },
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>(),
                maxItemCountExceeded: false);

            // Act
            var result = await _includeProcessor.ProcessIncludesAsync(
                "Patient", 
                queryParameters, 
                originalResult, 
                CancellationToken.None);

            // Assert
            Assert.Same(originalResult, result);
            Assert.Single(result.Results);
        }

        [Fact]
        public async Task ProcessIncludesAsync_WithIncludeParameters_ProcessesIncludes()
        {
            // Arrange
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_include", "Patient:organization")
            };

            var originalResult = new SearchResult(
                results: new List<SearchResultEntry>
                {
                    CreateSearchResultEntry("Patient", "1")
                },
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>(),
                maxItemCountExceeded: false);

            // Setup server responses with included resources
            var server1Result = new ServerSearchResult
            {
                ServerId = "server1",
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org1")
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>(),
                    maxItemCountExceeded: false)
            };

            var server2Result = new ServerSearchResult
            {
                ServerId = "server2",
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org2")
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>(),
                    maxItemCountExceeded: false)
            };

            _serverOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>
            {
                new FhirServerEndpoint { Id = "server1", BaseUrl = "https://server1.com/fhir", IsEnabled = true },
                new FhirServerEndpoint { Id = "server2", BaseUrl = "https://server2.com/fhir", IsEnabled = true }
            });

            _serverOrchestrator.SearchAsync(Arg.Any<FhirServerEndpoint>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(server1Result, server2Result);

            // Act
            var result = await _includeProcessor.ProcessIncludesAsync(
                "Patient", 
                queryParameters, 
                originalResult, 
                CancellationToken.None);

            // Assert
            Assert.NotSame(originalResult, result);
            Assert.Equal(3, result.Results.Count()); // Original Patient + 2 Organizations
            
            // Verify we have the original patient and the included organizations
            var resourceTypes = result.Results.Select(r => r.Resource.ResourceType.ToString()).ToList();
            Assert.Contains("Patient", resourceTypes);
            Assert.Contains("Organization", resourceTypes);
            Assert.Equal(2, resourceTypes.Count(rt => rt == "Organization"));
        }

        [Fact]
        public async Task ProcessIncludesOperationAsync_ReturnsIncludedResourcesOnly()
        {
            // Arrange
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("_include", "Patient:organization"),
                new Tuple<string, string>("_count", "50")
            };

            var serverResult = new ServerSearchResult
            {
                ServerId = "server1",
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org1"),
                        CreateSearchResultEntry("Organization", "org2")
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>(),
                    maxItemCountExceeded: false)
            };

            _serverOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>
            {
                new FhirServerEndpoint { Id = "server1", BaseUrl = "https://server1.com/fhir", IsEnabled = true }
            });

            _serverOrchestrator.SearchAsync(Arg.Any<FhirServerEndpoint>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(serverResult);

            // Act
            var result = await _includeProcessor.ProcessIncludesOperationAsync(
                "Patient", 
                queryParameters, 
                CancellationToken.None);

            // Assert
            Assert.Equal(2, result.Results.Count());
            Assert.All(result.Results, r => Assert.Equal("Organization", r.Resource.ResourceType.ToString()));
        }

        [Fact]
        public async Task ProcessIncludesAsync_HandlesDuplicateResources()
        {
            // Arrange
            var queryParameters = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("name", "John"),
                new Tuple<string, string>("_include", "Patient:organization")
            };

            var originalResult = new SearchResult(
                results: new List<SearchResultEntry>
                {
                    CreateSearchResultEntry("Patient", "1")
                },
                continuationToken: null,
                sortOrder: null,
                unsupportedSearchParameters: new List<Tuple<string, string>>(),
                maxItemCountExceeded: false);

            // Setup servers to return the same organization (duplicate)
            var serverResult1 = new ServerSearchResult
            {
                ServerId = "server1",
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org1")
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>(),
                    maxItemCountExceeded: false)
            };

            var serverResult2 = new ServerSearchResult
            {
                ServerId = "server2", 
                IsSuccess = true,
                SearchResult = new SearchResult(
                    results: new List<SearchResultEntry>
                    {
                        CreateSearchResultEntry("Organization", "org1") // Same org ID
                    },
                    continuationToken: null,
                    sortOrder: null,
                    unsupportedSearchParameters: new List<Tuple<string, string>>(),
                    maxItemCountExceeded: false)
            };

            _serverOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>
            {
                new FhirServerEndpoint { Id = "server1", BaseUrl = "https://server1.com/fhir", IsEnabled = true },
                new FhirServerEndpoint { Id = "server2", BaseUrl = "https://server2.com/fhir", IsEnabled = true }
            });

            _serverOrchestrator.SearchAsync(Arg.Any<FhirServerEndpoint>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(serverResult1, serverResult2);

            // Act
            var result = await _includeProcessor.ProcessIncludesAsync(
                "Patient", 
                queryParameters, 
                originalResult, 
                CancellationToken.None);

            // Assert
            Assert.Equal(2, result.Results.Count()); // Original Patient + 1 deduplicated Organization
            
            var organizations = result.Results.Where(r => r.Resource.ResourceType.ToString() == "Organization").ToList();
            Assert.Single(organizations); // Only one organization despite duplicate from two servers
            Assert.Equal("org1", organizations.First().Resource.Id);
        }

        private SearchResultEntry CreateSearchResultEntry(string resourceType, string id)
        {
            var resource = new Patient { Id = id };
            if (resourceType == "Organization")
            {
                resource = new Organization { Id = id };
            }

            return new SearchResultEntry(
                resource: resource, 
                searchEntryMode: Bundle.SearchEntryMode.Match);
        }
    }
}