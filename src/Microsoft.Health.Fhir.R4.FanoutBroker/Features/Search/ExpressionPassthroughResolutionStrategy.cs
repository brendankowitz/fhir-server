// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Expression-based passthrough resolution strategy that maintains existing behavior
    /// for co-located scenarios while providing the expression-based interface.
    /// This strategy provides backward compatibility and is suitable when resources are not distributed.
    /// </summary>
    public class ExpressionPassthroughResolutionStrategy : IExpressionResolutionStrategy
    {
        private readonly ILogger<ExpressionPassthroughResolutionStrategy> _logger;

        public ExpressionPassthroughResolutionStrategy(
            ILogger<ExpressionPassthroughResolutionStrategy> logger)
        {
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public bool CanProcess(Expression searchExpression)
        {
            // This strategy can handle any expression but is optimized for simple scenarios
            return true;
        }

        /// <inheritdoc />
        public Task<Expression> ProcessChainedSearchAsync(
            Expression searchExpression,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(searchExpression, nameof(searchExpression));

            _logger.LogDebug("Using passthrough strategy for chained search - returning original expression");

            // In passthrough mode, we don't modify the expression
            // The underlying FHIR server will handle chained searches directly
            return Task.FromResult(searchExpression);
        }

        /// <inheritdoc />
        public Task<SearchResult> ProcessIncludesAsync(
            Expression searchExpression,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(searchExpression, nameof(searchExpression));
            EnsureArg.IsNotNull(mainSearchResult, nameof(mainSearchResult));

            _logger.LogDebug("Using passthrough strategy for includes - returning main search result");

            // In passthrough mode, we assume includes are already processed by the underlying FHIR server
            // or that all resources are co-located and don't require distributed processing
            return Task.FromResult(mainSearchResult);
        }
    }
}