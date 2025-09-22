// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Factory for creating resolution strategies based on configuration.
    /// </summary>
    public interface IResolutionStrategyFactory
    {
        /// <summary>
        /// Creates the appropriate resolution strategy for chained searches based on configuration.
        /// </summary>
        /// <returns>The configured chained search resolution strategy.</returns>
        IResolutionStrategy CreateChainedSearchStrategy();

        /// <summary>
        /// Creates the appropriate resolution strategy for includes based on configuration.
        /// </summary>
        /// <returns>The configured include resolution strategy.</returns>
        IResolutionStrategy CreateIncludeStrategy();
    }
}