// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Factory for creating resolution strategies based on configuration.
    /// Supports both Passthrough and Distributed resolution modes.
    /// </summary>
    public class ResolutionStrategyFactory : IResolutionStrategyFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<ResolutionStrategyFactory> _logger;

        public ResolutionStrategyFactory(
            IServiceProvider serviceProvider,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<ResolutionStrategyFactory> logger)
        {
            _serviceProvider = EnsureArg.IsNotNull(serviceProvider, nameof(serviceProvider));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public IResolutionStrategy CreateChainedSearchStrategy()
        {
            var mode = _configuration.Value.ChainedSearchResolution;

            _logger.LogDebug("Creating chained search strategy for mode: {Mode}", mode);

            return mode switch
            {
                ResolutionMode.Passthrough => _serviceProvider.GetRequiredService<PassthroughResolutionStrategy>(),
                ResolutionMode.Distributed => _serviceProvider.GetRequiredService<DistributedResolutionStrategy>(),
                _ => throw new ArgumentException($"Unknown chained search resolution mode: {mode}")
            };
        }

        /// <inheritdoc />
        public IResolutionStrategy CreateIncludeStrategy()
        {
            var mode = _configuration.Value.IncludeResolution;

            _logger.LogDebug("Creating include strategy for mode: {Mode}", mode);

            return mode switch
            {
                ResolutionMode.Passthrough => _serviceProvider.GetRequiredService<PassthroughResolutionStrategy>(),
                ResolutionMode.Distributed => _serviceProvider.GetRequiredService<DistributedResolutionStrategy>(),
                _ => throw new ArgumentException($"Unknown include resolution mode: {mode}")
            };
        }
    }
}