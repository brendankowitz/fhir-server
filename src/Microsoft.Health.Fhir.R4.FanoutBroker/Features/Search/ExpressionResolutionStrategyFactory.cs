// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Factory for creating expression-based resolution strategies with intelligent strategy selection
    /// based on expression complexity and configuration settings.
    /// </summary>
    public class ExpressionResolutionStrategyFactory : IExpressionResolutionStrategyFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<ExpressionResolutionStrategyFactory> _logger;

        public ExpressionResolutionStrategyFactory(
            IServiceProvider serviceProvider,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<ExpressionResolutionStrategyFactory> logger)
        {
            _serviceProvider = EnsureArg.IsNotNull(serviceProvider, nameof(serviceProvider));
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public IExpressionResolutionStrategy CreateStrategy(Expression searchExpression)
        {
            EnsureArg.IsNotNull(searchExpression, nameof(searchExpression));

            // Analyze expression complexity to determine the best strategy
            var hasChainedSearches = ChainedSearchExtractionVisitor.HasChainedSearches(searchExpression);
            var hasIncludes = IncludeExtractionVisitor.HasIncludes(searchExpression);
            var hasIterativeIncludes = IncludeExtractionVisitor.HasIterativeIncludes(searchExpression);
            var hasWildcardIncludes = IncludeExtractionVisitor.HasWildcardIncludes(searchExpression);
            var hasCircularIncludes = IncludeExtractionVisitor.HasCircularReferenceIncludes(searchExpression);

            _logger.LogDebug("Expression analysis: ChainedSearches={HasChained}, Includes={HasIncludes}, " +
                            "IterativeIncludes={HasIterative}, WildcardIncludes={HasWildcard}, CircularIncludes={HasCircular}",
                hasChainedSearches, hasIncludes, hasIterativeIncludes, hasWildcardIncludes, hasCircularIncludes);

            // Determine strategy based on configuration and complexity
            var useDistributedStrategy = ShouldUseDistributedStrategy(
                hasChainedSearches, hasIncludes, hasIterativeIncludes, hasWildcardIncludes, hasCircularIncludes);

            if (useDistributedStrategy)
            {
                _logger.LogDebug("Creating ExpressionDistributedResolutionStrategy for complex expression");
                return _serviceProvider.GetRequiredService<ExpressionDistributedResolutionStrategy>();
            }
            else
            {
                _logger.LogDebug("Creating ExpressionPassthroughResolutionStrategy for simple expression");
                return _serviceProvider.GetRequiredService<ExpressionPassthroughResolutionStrategy>();
            }
        }

        /// <inheritdoc />
        public IExpressionResolutionStrategy CreateChainedSearchStrategy()
        {
            var mode = _configuration.Value.ChainedSearchResolution;

            _logger.LogDebug("Creating chained search strategy for mode: {Mode}", mode);

            return mode switch
            {
                ResolutionMode.Passthrough => _serviceProvider.GetRequiredService<ExpressionPassthroughResolutionStrategy>(),
                ResolutionMode.Distributed => _serviceProvider.GetRequiredService<ExpressionDistributedResolutionStrategy>(),
                _ => throw new ArgumentException($"Unknown chained search resolution mode: {mode}")
            };
        }

        /// <inheritdoc />
        public IExpressionResolutionStrategy CreateIncludeStrategy()
        {
            var mode = _configuration.Value.IncludeResolution;

            _logger.LogDebug("Creating include strategy for mode: {Mode}", mode);

            return mode switch
            {
                ResolutionMode.Passthrough => _serviceProvider.GetRequiredService<ExpressionPassthroughResolutionStrategy>(),
                ResolutionMode.Distributed => _serviceProvider.GetRequiredService<ExpressionDistributedResolutionStrategy>(),
                _ => throw new ArgumentException($"Unknown include resolution mode: {mode}")
            };
        }

        /// <summary>
        /// Determines whether to use distributed strategy based on expression complexity and configuration.
        /// </summary>
        private bool ShouldUseDistributedStrategy(
            bool hasChainedSearches,
            bool hasIncludes,
            bool hasIterativeIncludes,
            bool hasWildcardIncludes,
            bool hasCircularIncludes)
        {
            // Always use distributed strategy for complex scenarios that require cross-shard coordination
            if (hasIterativeIncludes || hasWildcardIncludes || hasCircularIncludes)
            {
                return true;
            }

            // Use configuration-based decision for simpler scenarios
            if (hasChainedSearches && _configuration.Value.ChainedSearchResolution == ResolutionMode.Distributed)
            {
                return true;
            }

            if (hasIncludes && _configuration.Value.IncludeResolution == ResolutionMode.Distributed)
            {
                return true;
            }

            return false;
        }
    }
}