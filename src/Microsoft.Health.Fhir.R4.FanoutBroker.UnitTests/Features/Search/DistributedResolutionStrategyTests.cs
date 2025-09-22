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
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Models;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.R4.FanoutBroker.UnitTests.Features.Search
{
    public class DistributedResolutionStrategyTests
    {
        private readonly IFhirServerOrchestrator _mockOrchestrator;
        private readonly ISearchOptionsFactory _mockSearchOptionsFactory;
        private readonly IOptions<FanoutBrokerConfiguration> _mockConfiguration;
        private readonly ILogger<DistributedResolutionStrategy> _mockLogger;
        private readonly DistributedResolutionStrategy _strategy;

        public DistributedResolutionStrategyTests()
        {
            _mockOrchestrator = Substitute.For<IFhirServerOrchestrator>();
            _mockSearchOptionsFactory = Substitute.For<ISearchOptionsFactory>();
            _mockLogger = Substitute.For<ILogger<DistributedResolutionStrategy>>();

            var config = new FanoutBrokerConfiguration
            {
                DistributedChainTimeoutSeconds = 15,
                DistributedIncludeTimeoutSeconds = 10,
                MaxDistributedReferenceIds = 1000,
                DistributedBatchSize = 100
            };
            _mockConfiguration = Options.Create(config);

            _strategy = new DistributedResolutionStrategy(
                _mockOrchestrator,
                _mockSearchOptionsFactory,
                _mockConfiguration,
                _mockLogger);
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_WithNoChainedParameters_ReturnsOriginalParameters()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("status", "final"),
                Tuple.Create("category", "laboratory")
            };

            // Act
            var result = await _strategy.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);

            // Assert
            Assert.Equal(queryParameters.Count, result.Count);
            Assert.Equal("status", result[0].Item1);
            Assert.Equal("final", result[0].Item2);
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_WithChainedParameter_DetectsChain()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("subject:Patient.name", "Sarah"),
                Tuple.Create("status", "final")
            };

            // Setup mock for empty server list to avoid actual search execution
            _mockOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>());

            // Act
            var result = await _strategy.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);

            // Assert - should detect the chained parameter
            Assert.Contains(result, p => p.Item1 == "status" && p.Item2 == "final");
            // The chained parameter should be removed (since no servers returned results)
            Assert.DoesNotContain(result, p => p.Item1 == "subject:Patient.name");
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_WithSimpleChainedParameter_ParsesCorrectly()
        {
            // Arrange
            var resourceType = "Observation";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("subject.name", "John"),
                Tuple.Create("code", "vital-signs")
            };

            // Setup mock for empty server list
            _mockOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>());

            // Act
            var result = await _strategy.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);

            // Assert - should detect the simple chained parameter
            Assert.Contains(result, p => p.Item1 == "code" && p.Item2 == "vital-signs");
            // The chained parameter should be processed
            Assert.DoesNotContain(result, p => p.Item1 == "subject.name");
        }

        [Fact]
        public async Task ProcessIncludesAsync_WithNoIncludeParameters_ReturnsMainResult()
        {
            // Arrange
            var resourceType = "Patient";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("name", "John"),
                Tuple.Create("birthdate", "1980-01-01")
            };
            var mainResult = new SearchResult(new List<SearchResultEntry>(), null, null, null);

            // Act
            var result = await _strategy.ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None);

            // Assert
            Assert.Same(mainResult, result);
        }

        [Fact]
        public async Task ProcessIncludesAsync_WithIncludeParameters_DetectsIncludes()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("_include", "DiagnosticReport:subject"),
                Tuple.Create("status", "final")
            };
            var mainResult = CreateMockSearchResult("DiagnosticReport", new[] { "dr1", "dr2" });

            // Setup mock for empty server list to avoid actual search execution
            _mockOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>());

            // Act
            var result = await _strategy.ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None);

            // Assert - should return main result when no servers available
            Assert.Equal(mainResult.Results?.Count(), result.Results?.Count());
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_WithMultipleChainedParameters_ProcessesAll()
        {
            // Arrange
            var resourceType = "Observation";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("subject:Patient.name", "John"),
                Tuple.Create("performer:Practitioner.name", "Smith"),
                Tuple.Create("code", "vital-signs")
            };

            // Setup mock for empty server list
            _mockOrchestrator.GetEnabledServers().Returns(new List<FhirServerEndpoint>());

            // Act
            var result = await _strategy.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);

            // Assert - should process both chained parameters
            Assert.Contains(result, p => p.Item1 == "code" && p.Item2 == "vital-signs");
            Assert.DoesNotContain(result, p => p.Item1 == "subject:Patient.name");
            Assert.DoesNotContain(result, p => p.Item1 == "performer:Practitioner.name");
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_WithSuccessfulChainResolution_ReplacesChainWithIds()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("subject:Patient.name", "Sarah"),
                Tuple.Create("status", "final")
            };

            // Setup mock servers and search results
            var mockServers = new List<FhirServerEndpoint>
            {
                new FhirServerEndpoint { BaseUrl = "https://server1.com", IsEnabled = true },
                new FhirServerEndpoint { BaseUrl = "https://server2.com", IsEnabled = true }
            };
            _mockOrchestrator.GetEnabledServers().Returns(mockServers);

            // Setup successful search results from both servers
            var server1Result = new ServerSearchResult
            {
                IsSuccess = true,
                SearchResult = CreateMockSearchResult("Patient", new[] { "patient1", "patient2" })
            };
            var server2Result = new ServerSearchResult
            {
                IsSuccess = true,
                SearchResult = CreateMockSearchResult("Patient", new[] { "patient3" })
            };

            _mockOrchestrator.SearchAsync(mockServers[0], Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(server1Result));
            _mockOrchestrator.SearchAsync(mockServers[1], Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(server2Result));

            _mockSearchOptionsFactory.Create(
                Arg.Is("Patient"),
                Arg.Any<IReadOnlyList<Tuple<string, string>>>(),
                Arg.Any<bool>(),
                Arg.Any<ResourceVersionType>(),
                Arg.Any<bool>())
                .Returns(new SearchOptions());

            // Act
            var result = await _strategy.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);

            // Assert
            Assert.Contains(result, p => p.Item1 == "status" && p.Item2 == "final");
            Assert.DoesNotContain(result, p => p.Item1 == "subject:Patient.name");
            // Should have replaced with subject parameter containing resolved IDs
            var subjectParam = result.FirstOrDefault(p => p.Item1 == "subject");
            Assert.NotNull(subjectParam);
            Assert.Contains("extracted-id", subjectParam.Item2); // Our mock returns extracted-id-{hashcode}
        }

        [Fact]
        public async Task ProcessIncludesAsync_WithSuccessfulIncludeResolution_MergesResults()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("_include", "DiagnosticReport:subject"),
                Tuple.Create("status", "final")
            };
            var mainResult = CreateMockSearchResult("DiagnosticReport", new[] { "dr1", "dr2" });

            // Setup mock servers
            var mockServers = new List<FhirServerEndpoint>
            {
                new FhirServerEndpoint { BaseUrl = "https://server1.com", IsEnabled = true }
            };
            _mockOrchestrator.GetEnabledServers().Returns(mockServers);

            // Setup successful include search results
            var includeResult = new ServerSearchResult
            {
                IsSuccess = true,
                SearchResult = CreateMockSearchResult("Patient", new[] { "patient1", "patient2" })
            };

            _mockOrchestrator.SearchAsync(mockServers[0], Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(includeResult));

            _mockSearchOptionsFactory.Create(
                Arg.Is("Patient"),
                Arg.Any<IReadOnlyList<Tuple<string, string>>>(),
                Arg.Any<bool>(),
                Arg.Any<ResourceVersionType>(),
                Arg.Any<bool>())
                .Returns(new SearchOptions());

            // Act
            var result = await _strategy.ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None);

            // Assert
            Assert.NotNull(result.Results);
            // Should contain both main results and include results
            var totalExpected = (mainResult.Results?.Count() ?? 0) + (includeResult.SearchResult.Results?.Count() ?? 0);
            Assert.Equal(totalExpected, result.Results.Count());
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_WithTimeoutConfiguration_RespectsTimeout()
        {
            // Arrange
            var config = new FanoutBrokerConfiguration
            {
                DistributedChainTimeoutSeconds = 1, // Very short timeout
                MaxDistributedReferenceIds = 1000
            };
            var shortTimeoutStrategy = new DistributedResolutionStrategy(
                _mockOrchestrator,
                _mockSearchOptionsFactory,
                Options.Create(config),
                _mockLogger);

            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("subject:Patient.name", "Sarah")
            };

            var mockServers = new List<FhirServerEndpoint>
            {
                new FhirServerEndpoint { BaseUrl = "https://slow-server.com", IsEnabled = true }
            };
            _mockOrchestrator.GetEnabledServers().Returns(mockServers);

            // Setup slow response that will timeout
            _mockOrchestrator.SearchAsync(Arg.Any<FhirServerEndpoint>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(async (args) =>
                {
                    var cancellationToken = (CancellationToken)args[2];
                    await Task.Delay(5000, cancellationToken); // Delay longer than timeout
                    return new ServerSearchResult { IsSuccess = true, SearchResult = new SearchResult(new List<SearchResultEntry>(), null, null, null) };
                });

            _mockSearchOptionsFactory.Create(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Tuple<string, string>>>(),
                Arg.Any<bool>(),
                Arg.Any<ResourceVersionType>(),
                Arg.Any<bool>())
                .Returns(new SearchOptions());

            // Act
            var result = await shortTimeoutStrategy.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);

            // Assert - should handle timeout gracefully and keep original parameters
            Assert.Contains(result, p => p.Item1 == "subject:Patient.name");
        }

        private static SearchResult CreateMockSearchResult(string resourceType, string[] resourceIds)
        {
            var entries = resourceIds.Select(id => new SearchResultEntry(
                new ResourceWrapper(
                    $"{resourceType}/{id}",
                    "1",
                    resourceType,
                    new RawResource($"{{\"resourceType\":\"{resourceType}\",\"id\":\"{id}\"}}", FhirResourceFormat.Json, isMetaSet: false),
                    null,
                    DateTimeOffset.UtcNow,
                    false,
                    null,
                    null,
                    null),
                null)).ToList();

            return new SearchResult(entries, null, null, null);
        }
    }
}