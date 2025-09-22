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
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.R4.FanoutBroker.UnitTests.Features.Search
{
    public class PassthroughResolutionStrategyTests
    {
        private readonly IChainedSearchProcessor _mockChainedSearchProcessor;
        private readonly IIncludeProcessor _mockIncludeProcessor;
        private readonly ILogger<PassthroughResolutionStrategy> _mockLogger;
        private readonly PassthroughResolutionStrategy _strategy;

        public PassthroughResolutionStrategyTests()
        {
            _mockChainedSearchProcessor = Substitute.For<IChainedSearchProcessor>();
            _mockIncludeProcessor = Substitute.For<IIncludeProcessor>();
            _mockLogger = Substitute.For<ILogger<PassthroughResolutionStrategy>>();

            _strategy = new PassthroughResolutionStrategy(
                _mockChainedSearchProcessor,
                _mockIncludeProcessor,
                _mockLogger);
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_CallsChainedSearchProcessor()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("subject:Patient.name", "Sarah"),
                Tuple.Create("status", "final"),
            };
            var expectedResult = new List<Tuple<string, string>>
            {
                Tuple.Create("subject", "patient1,patient2"),
                Tuple.Create("status", "final"),
            };

            _mockChainedSearchProcessor.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None)
                .Returns(Task.FromResult(expectedResult));

            // Act
            var result = await _strategy.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);

            // Assert
            Assert.Equal(expectedResult.Count, result.Count);
            Assert.Equal("subject", result[0].Item1);
            Assert.Equal("patient1,patient2", result[0].Item2);

            await _mockChainedSearchProcessor.Received(1).ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_WithNoChainedParameters_PassesThroughDirectly()
        {
            // Arrange
            var resourceType = "Patient";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("name", "John"),
                Tuple.Create("birthdate", "1980-01-01"),
            };

            _mockChainedSearchProcessor.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None)
                .Returns(Task.FromResult(queryParameters));

            // Act
            var result = await _strategy.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);

            // Assert
            Assert.Equal(queryParameters.Count, result.Count);
            Assert.Equal("name", result[0].Item1);
            Assert.Equal("John", result[0].Item2);

            await _mockChainedSearchProcessor.Received(1).ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None);
        }

        [Fact]
        public async Task ProcessIncludesAsync_CallsIncludeProcessor()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("_include", "DiagnosticReport:subject"),
                Tuple.Create("status", "final"),
            };
            var mainResult = CreateMockSearchResult("DiagnosticReport", new[] { "dr1", "dr2" });
            var expectedIncludeResult = CreateMockSearchResult("Patient", new[] { "patient1", "patient2" });
            var expectedMergedResult = MergeSearchResults(mainResult, expectedIncludeResult);

            _mockIncludeProcessor.ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None)
                .Returns(Task.FromResult(expectedMergedResult));

            // Act
            var result = await _strategy.ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None);

            // Assert
            Assert.Equal(expectedMergedResult.Results?.Count(), result.Results?.Count());

            await _mockIncludeProcessor.Received(1).ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None);
        }

        [Fact]
        public async Task ProcessIncludesAsync_WithNoIncludeParameters_ReturnsMainResult()
        {
            // Arrange
            var resourceType = "Patient";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("name", "John"),
                Tuple.Create("birthdate", "1980-01-01"),
            };
            var mainResult = CreateMockSearchResult("Patient", new[] { "patient1" });

            _mockIncludeProcessor.ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None)
                .Returns(Task.FromResult(mainResult));

            // Act
            var result = await _strategy.ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None);

            // Assert
            Assert.Same(mainResult, result);

            await _mockIncludeProcessor.Received(1).ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None);
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_HandlesExceptionGracefully()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("subject:Patient.name", "Sarah"),
            };

            _mockChainedSearchProcessor.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None)
                .Throws(new InvalidOperationException("Processing failed"));

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _strategy.ProcessChainedSearchAsync(resourceType, queryParameters, CancellationToken.None));
        }

        [Fact]
        public async Task ProcessIncludesAsync_HandlesExceptionGracefully()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("_include", "DiagnosticReport:subject"),
            };
            var mainResult = CreateMockSearchResult("DiagnosticReport", new[] { "dr1" });

            _mockIncludeProcessor.ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None)
                .Throws(new InvalidOperationException("Include processing failed"));

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _strategy.ProcessIncludesAsync(resourceType, queryParameters, mainResult, CancellationToken.None));
        }

        [Fact]
        public async Task ProcessChainedSearchAsync_WithCancellationToken_PropagatesToken()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("subject:Patient.name", "Sarah"),
            };
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            _mockChainedSearchProcessor.ProcessChainedSearchAsync(resourceType, queryParameters, cancellationToken)
                .Returns(Task.FromResult(queryParameters));

            // Act
            await _strategy.ProcessChainedSearchAsync(resourceType, queryParameters, cancellationToken);

            // Assert
            await _mockChainedSearchProcessor.Received(1).ProcessChainedSearchAsync(resourceType, queryParameters, cancellationToken);
        }

        [Fact]
        public async Task ProcessIncludesAsync_WithCancellationToken_PropagatesToken()
        {
            // Arrange
            var resourceType = "DiagnosticReport";
            var queryParameters = new List<Tuple<string, string>>
            {
                Tuple.Create("_include", "DiagnosticReport:subject"),
            };
            var mainResult = CreateMockSearchResult("DiagnosticReport", new[] { "dr1" });
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            _mockIncludeProcessor.ProcessIncludesAsync(resourceType, queryParameters, mainResult, cancellationToken)
                .Returns(Task.FromResult(mainResult));

            // Act
            await _strategy.ProcessIncludesAsync(resourceType, queryParameters, mainResult, cancellationToken);

            // Assert
            await _mockIncludeProcessor.Received(1).ProcessIncludesAsync(resourceType, queryParameters, mainResult, cancellationToken);
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

        private static SearchResult MergeSearchResults(SearchResult main, SearchResult include)
        {
            var allResults = new List<SearchResultEntry>();

            if (main.Results != null)
                allResults.AddRange(main.Results);

            if (include.Results != null)
                allResults.AddRange(include.Results);

            return new SearchResult(allResults, main.ContinuationToken, main.SortOrder, main.UnsupportedSearchParameters);
        }
    }
}