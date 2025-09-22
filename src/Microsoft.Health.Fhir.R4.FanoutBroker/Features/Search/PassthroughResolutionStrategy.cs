// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Features.Search;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Implements passthrough resolution strategy that assumes resources and their references
    /// are co-located on the same servers. This maintains the current behavior for optimal performance.
    /// </summary>
    public class PassthroughResolutionStrategy : IResolutionStrategy
    {
        private readonly IChainedSearchProcessor _chainedSearchProcessor;
        private readonly IIncludeProcessor _includeProcessor;
        private readonly ILogger<PassthroughResolutionStrategy> _logger;

        public PassthroughResolutionStrategy(
            IChainedSearchProcessor chainedSearchProcessor,
            IIncludeProcessor includeProcessor,
            ILogger<PassthroughResolutionStrategy> logger)
        {
            _chainedSearchProcessor = EnsureArg.IsNotNull(chainedSearchProcessor, nameof(chainedSearchProcessor));
            _includeProcessor = EnsureArg.IsNotNull(includeProcessor, nameof(includeProcessor));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Tuple<string, string>>> ProcessChainedSearchAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resourceType, nameof(resourceType));
            EnsureArg.IsNotNull(queryParameters, nameof(queryParameters));

            _logger.LogDebug("Processing chained search using Passthrough strategy (assumes co-located references)");

            // Use existing passthrough logic - assumes co-location
            var result = await _chainedSearchProcessor.ProcessChainedSearchAsync(
                resourceType, queryParameters, cancellationToken);

            return result;
        }

        /// <inheritdoc />
        public async Task<SearchResult> ProcessIncludesAsync(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            SearchResult mainSearchResult,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resourceType, nameof(resourceType));
            EnsureArg.IsNotNull(queryParameters, nameof(queryParameters));
            EnsureArg.IsNotNull(mainSearchResult, nameof(mainSearchResult));

            _logger.LogDebug("Processing includes using Passthrough strategy (assumes co-located references)");

            // Use existing passthrough logic - assumes co-location
            return await _includeProcessor.ProcessIncludesAsync(
                resourceType, queryParameters, mainSearchResult, cancellationToken);
        }
    }
}