// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.FanoutBroker.UnitTests.Features.Search
{
    /// <summary>
    /// Unit tests for the ExecutionStrategyAnalyzer.
    /// </summary>
    public class ExecutionStrategyAnalyzerTests
    {
        private readonly ExecutionStrategyAnalyzer _analyzer;

        public ExecutionStrategyAnalyzerTests()
        {
            _analyzer = new ExecutionStrategyAnalyzer();
        }

        [Fact]
        public void DetermineStrategy_WithSortParameter_ReturnsParallel()
        {
            // Arrange
            var searchOptions = CreateSearchOptions();
            var sortInfo = Substitute.For<SearchParameterInfo>();
            sortInfo.Name.Returns("_lastModified");
            
            searchOptions.Sort = new List<(SearchParameterInfo, SortOrder)>
            {
                (sortInfo, SortOrder.Ascending)
            };

            // Act
            var strategy = _analyzer.DetermineStrategy(searchOptions);

            // Assert
            Assert.Equal(ExecutionStrategy.Parallel, strategy);
        }

        [Fact]
        public void DetermineStrategy_WithSmallCount_ReturnsParallel()
        {
            // Arrange
            var searchOptions = CreateSearchOptions();
            searchOptions.MaxItemCount = 5;

            // Act
            var strategy = _analyzer.DetermineStrategy(searchOptions);

            // Assert
            Assert.Equal(ExecutionStrategy.Parallel, strategy);
        }

        [Fact]
        public void DetermineStrategy_WithLargeCount_ReturnsSequential()
        {
            // Arrange
            var searchOptions = CreateSearchOptions();
            searchOptions.MaxItemCount = 50;

            // Act
            var strategy = _analyzer.DetermineStrategy(searchOptions);

            // Assert
            Assert.Equal(ExecutionStrategy.Sequential, strategy);
        }

        [Fact]
        public void DetermineStrategy_WithNoSpecialConditions_ReturnsParallel()
        {
            // Arrange
            var searchOptions = CreateSearchOptions();
            searchOptions.MaxItemCount = 15; // Between thresholds

            // Act
            var strategy = _analyzer.DetermineStrategy(searchOptions);

            // Assert
            Assert.Equal(ExecutionStrategy.Parallel, strategy);
        }

        private SearchOptions CreateSearchOptions()
        {
            var searchOptions = Substitute.For<SearchOptions>();
            searchOptions.Sort.Returns(new List<(SearchParameterInfo, SortOrder)>());
            searchOptions.UnsupportedSearchParams.Returns(new List<Tuple<string, string>>());
            return searchOptions;
        }
    }
}