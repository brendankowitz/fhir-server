// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Factory for creating expression-based resolution strategies based on configuration and expression complexity.
    /// This provides intelligent strategy selection for optimal handling of different FHIR search scenarios.
    /// </summary>
    public interface IExpressionResolutionStrategyFactory
    {
        /// <summary>
        /// Creates the most appropriate resolution strategy for the given search expression.
        /// This allows for intelligent strategy selection based on expression complexity and configuration.
        /// </summary>
        /// <param name="searchExpression">The search expression to analyze for strategy selection.</param>
        /// <returns>The most suitable resolution strategy for the expression.</returns>
        IExpressionResolutionStrategy CreateStrategy(Expression searchExpression);

        /// <summary>
        /// Creates a strategy specifically optimized for chained search processing.
        /// </summary>
        /// <returns>A resolution strategy optimized for chained searches.</returns>
        IExpressionResolutionStrategy CreateChainedSearchStrategy();

        /// <summary>
        /// Creates a strategy specifically optimized for include processing.
        /// </summary>
        /// <returns>A resolution strategy optimized for includes.</returns>
        IExpressionResolutionStrategy CreateIncludeStrategy();
    }
}