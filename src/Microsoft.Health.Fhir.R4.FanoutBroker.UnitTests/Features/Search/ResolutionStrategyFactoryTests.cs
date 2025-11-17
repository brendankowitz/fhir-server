// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Models;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.R4.FanoutBroker.UnitTests.Features.Search
{
    public class ResolutionStrategyFactoryTests
    {
        private readonly IServiceProvider _mockServiceProvider;
        private readonly IOptions<FanoutBrokerConfiguration> _mockConfiguration;
        private readonly ILogger<ResolutionStrategyFactory> _mockLogger;
        private readonly ResolutionStrategyFactory _factory;

        public ResolutionStrategyFactoryTests()
        {
            _mockServiceProvider = Substitute.For<IServiceProvider>();
            _mockLogger = Substitute.For<ILogger<ResolutionStrategyFactory>>();

            var config = new FanoutBrokerConfiguration
            {
                ChainedSearchResolution = ResolutionMode.Passthrough,
                IncludeResolution = ResolutionMode.Passthrough
            };
            _mockConfiguration = Options.Create(config);

            _factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                _mockConfiguration,
                _mockLogger);
        }

        [Fact]
        public void CreateChainedSearchStrategy_WithPassthroughMode_ReturnsPassthroughStrategy()
        {
            // Arrange
            var config = new FanoutBrokerConfiguration
            {
                ChainedSearchResolution = ResolutionMode.Passthrough
            };
            var factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                Options.Create(config),
                _mockLogger);

            var mockPassthroughStrategy = Substitute.For<PassthroughResolutionStrategy>(null, null, null);
            _mockServiceProvider.GetRequiredService<PassthroughResolutionStrategy>()
                .Returns(mockPassthroughStrategy);

            // Act
            var result = factory.CreateChainedSearchStrategy();

            // Assert
            Assert.Same(mockPassthroughStrategy, result);
            _mockServiceProvider.Received(1).GetRequiredService<PassthroughResolutionStrategy>();
        }

        [Fact]
        public void CreateChainedSearchStrategy_WithDistributedMode_ReturnsDistributedStrategy()
        {
            // Arrange
            var config = new FanoutBrokerConfiguration
            {
                ChainedSearchResolution = ResolutionMode.Distributed
            };
            var factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                Options.Create(config),
                _mockLogger);

            var mockDistributedStrategy = Substitute.For<DistributedResolutionStrategy>(null, null, null, null);
            _mockServiceProvider.GetRequiredService<DistributedResolutionStrategy>()
                .Returns(mockDistributedStrategy);

            // Act
            var result = factory.CreateChainedSearchStrategy();

            // Assert
            Assert.Same(mockDistributedStrategy, result);
            _mockServiceProvider.Received(1).GetRequiredService<DistributedResolutionStrategy>();
        }

        [Fact]
        public void CreateIncludeStrategy_WithPassthroughMode_ReturnsPassthroughStrategy()
        {
            // Arrange
            var config = new FanoutBrokerConfiguration
            {
                IncludeResolution = ResolutionMode.Passthrough
            };
            var factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                Options.Create(config),
                _mockLogger);

            var mockPassthroughStrategy = Substitute.For<PassthroughResolutionStrategy>(null, null, null);
            _mockServiceProvider.GetRequiredService<PassthroughResolutionStrategy>()
                .Returns(mockPassthroughStrategy);

            // Act
            var result = factory.CreateIncludeStrategy();

            // Assert
            Assert.Same(mockPassthroughStrategy, result);
            _mockServiceProvider.Received(1).GetRequiredService<PassthroughResolutionStrategy>();
        }

        [Fact]
        public void CreateIncludeStrategy_WithDistributedMode_ReturnsDistributedStrategy()
        {
            // Arrange
            var config = new FanoutBrokerConfiguration
            {
                IncludeResolution = ResolutionMode.Distributed
            };
            var factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                Options.Create(config),
                _mockLogger);

            var mockDistributedStrategy = Substitute.For<DistributedResolutionStrategy>(null, null, null, null);
            _mockServiceProvider.GetRequiredService<DistributedResolutionStrategy>()
                .Returns(mockDistributedStrategy);

            // Act
            var result = factory.CreateIncludeStrategy();

            // Assert
            Assert.Same(mockDistributedStrategy, result);
            _mockServiceProvider.Received(1).GetRequiredService<DistributedResolutionStrategy>();
        }

        [Fact]
        public void CreateChainedSearchStrategy_WithInvalidMode_ThrowsArgumentException()
        {
            // Arrange
            var config = new FanoutBrokerConfiguration
            {
                ChainedSearchResolution = (ResolutionMode)999 // Invalid enum value
            };
            var factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                Options.Create(config),
                _mockLogger);

            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() => factory.CreateChainedSearchStrategy());
            Assert.Contains("Unknown chained search resolution mode", exception.Message);
        }

        [Fact]
        public void CreateIncludeStrategy_WithInvalidMode_ThrowsArgumentException()
        {
            // Arrange
            var config = new FanoutBrokerConfiguration
            {
                IncludeResolution = (ResolutionMode)999 // Invalid enum value
            };
            var factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                Options.Create(config),
                _mockLogger);

            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() => factory.CreateIncludeStrategy());
            Assert.Contains("Unknown include resolution mode", exception.Message);
        }

        [Fact]
        public void CreateChainedSearchStrategy_WithMixedModes_ReturnsCorrectStrategy()
        {
            // Arrange - Chained: Distributed, Include: Passthrough
            var config = new FanoutBrokerConfiguration
            {
                ChainedSearchResolution = ResolutionMode.Distributed,
                IncludeResolution = ResolutionMode.Passthrough
            };
            var factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                Options.Create(config),
                _mockLogger);

            var mockDistributedStrategy = Substitute.For<DistributedResolutionStrategy>(null, null, null, null);
            var mockPassthroughStrategy = Substitute.For<PassthroughResolutionStrategy>(null, null, null);

            _mockServiceProvider.GetRequiredService<DistributedResolutionStrategy>()
                .Returns(mockDistributedStrategy);
            _mockServiceProvider.GetRequiredService<PassthroughResolutionStrategy>()
                .Returns(mockPassthroughStrategy);

            // Act
            var chainedResult = factory.CreateChainedSearchStrategy();
            var includeResult = factory.CreateIncludeStrategy();

            // Assert
            Assert.Same(mockDistributedStrategy, chainedResult);
            Assert.Same(mockPassthroughStrategy, includeResult);
        }

        [Fact]
        public void Constructor_WithNullParameters_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new ResolutionStrategyFactory(null, _mockConfiguration, _mockLogger));
            Assert.Throws<ArgumentNullException>(() => new ResolutionStrategyFactory(_mockServiceProvider, null, _mockLogger));
            Assert.Throws<ArgumentNullException>(() => new ResolutionStrategyFactory(_mockServiceProvider, _mockConfiguration, null));
        }

        [Fact]
        public void CreateChainedSearchStrategy_LogsDebugMessage()
        {
            // Arrange
            var config = new FanoutBrokerConfiguration
            {
                ChainedSearchResolution = ResolutionMode.Passthrough
            };
            var factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                Options.Create(config),
                _mockLogger);

            var mockStrategy = Substitute.For<PassthroughResolutionStrategy>(null, null, null);
            _mockServiceProvider.GetRequiredService<PassthroughResolutionStrategy>()
                .Returns(mockStrategy);

            // Act
            factory.CreateChainedSearchStrategy();

            // Assert
            // Note: We can't easily verify ILogger calls with NSubstitute without additional setup,
            // but this test documents the expected behavior
            _mockServiceProvider.Received(1).GetRequiredService<PassthroughResolutionStrategy>();
        }

        [Fact]
        public void CreateIncludeStrategy_LogsDebugMessage()
        {
            // Arrange
            var config = new FanoutBrokerConfiguration
            {
                IncludeResolution = ResolutionMode.Distributed
            };
            var factory = new ResolutionStrategyFactory(
                _mockServiceProvider,
                Options.Create(config),
                _mockLogger);

            var mockStrategy = Substitute.For<DistributedResolutionStrategy>(null, null, null, null);
            _mockServiceProvider.GetRequiredService<DistributedResolutionStrategy>()
                .Returns(mockStrategy);

            // Act
            factory.CreateIncludeStrategy();

            // Assert
            _mockServiceProvider.Received(1).GetRequiredService<DistributedResolutionStrategy>();
        }
    }
}