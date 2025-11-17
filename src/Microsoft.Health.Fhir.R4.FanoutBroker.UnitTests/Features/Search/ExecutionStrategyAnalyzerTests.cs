// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;
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
            // Arrange: create search options with a dummy sort entry (content not inspected by analyzer)
            var searchOptions = CreateSearchOptions(so =>
            {
                SetProperty(so, "Sort", new List<(SearchParameterInfo, SortOrder)>
                {
                    (null, SortOrder.Ascending),
                });
            });

            // Act
            var strategy = _analyzer.DetermineStrategy(searchOptions);

            // Assert
            Assert.Equal(ExecutionStrategy.Parallel, strategy);
        }

        [Fact]
        public void DetermineStrategy_WithSmallCount_ReturnsParallel()
        {
            // Arrange (value <= ParallelCountThreshold)
            var searchOptions = CreateSearchOptions(so => SetProperty(so, "MaxItemCount", 5));

            // Act
            var strategy = _analyzer.DetermineStrategy(searchOptions);

            // Assert
            Assert.Equal(ExecutionStrategy.Parallel, strategy);
        }

        [Fact]
        public void DetermineStrategy_WithLargeCount_ReturnsSequential()
        {
            // Arrange (value > SequentialCountThreshold)
            var searchOptions = CreateSearchOptions(so => SetProperty(so, "MaxItemCount", 50));

            // Act
            var strategy = _analyzer.DetermineStrategy(searchOptions);

            // Assert
            Assert.Equal(ExecutionStrategy.Sequential, strategy);
        }

        [Fact]
        public void DetermineStrategy_WithNoSpecialConditions_ReturnsParallel()
        {
            // Arrange (between thresholds and no special parameters)
            var searchOptions = CreateSearchOptions(so => SetProperty(so, "MaxItemCount", 15));

            // Act
            var strategy = _analyzer.DetermineStrategy(searchOptions);

            // Assert
            Assert.Equal(ExecutionStrategy.Parallel, strategy);
        }

        private SearchOptions CreateSearchOptions(Action<SearchOptions> configure = null)
        {
            // Instantiate via the internal constructor using reflection
            var searchOptions = (SearchOptions)Activator.CreateInstance(typeof(SearchOptions), nonPublic: true);

            // Initialize properties the analyzer may access
            SetProperty(searchOptions, "Sort", new List<(SearchParameterInfo, SortOrder)>());
            SetProperty(searchOptions, "UnsupportedSearchParams", new List<Tuple<string, string>>());

            configure?.Invoke(searchOptions);
            return searchOptions;
        }

        private static void SetProperty(object instance, string propertyName, object value)
        {
            var prop = instance.GetType().GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            prop.SetValue(instance, value);
        }
    }
}
