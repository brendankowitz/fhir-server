// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Simplified search options factory for the fanout broker service.
    /// This implementation creates basic SearchOptions without complex expression parsing.
    /// Note: This is a simplified implementation that doesn't support full FHIR search parsing.
    /// The fanout broker forwards queries to downstream servers which handle the actual parsing.
    /// </summary>
    public class FanoutSearchOptionsFactory : ISearchOptionsFactory
    {
        /// <summary>
        /// Creates basic search options for the fanout broker service.
        /// Note: This implementation is limited and primarily serves as a placeholder.
        /// The actual search parsing is handled by downstream FHIR servers.
        /// </summary>
        public SearchOptions Create(
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            bool isAsyncOperation = false,
            ResourceVersionType resourceVersionTypes = ResourceVersionType.Latest,
            bool onlyIds = false,
            bool isIncludesOperation = false)
        {
            // Since SearchOptions constructor is internal, we need a different approach
            // For the fanout broker implementation, we'll need to work around this limitation
            // by creating a basic search options instance through reflection or other means
            
            // This is a placeholder implementation that will need to be replaced with proper
            // SearchOptions creation once we integrate with the full FHIR search infrastructure
            throw new NotImplementedException(
                "Full SearchOptions creation requires integration with the FHIR search infrastructure. " +
                "The fanout broker currently acts as a query proxy forwarding searches to downstream servers.");
        }

        /// <summary>
        /// Creates search options for compartment searches (not supported in fanout broker).
        /// </summary>
        public SearchOptions Create(
            string compartmentType,
            string compartmentId,
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            bool isAsyncOperation = false,
            bool useSmartCompartmentDefinition = false,
            ResourceVersionType resourceVersionTypes = ResourceVersionType.Latest,
            bool onlyIds = false,
            bool isIncludesOperation = false)
        {
            throw new NotSupportedException("Compartment searches are not supported in the fanout broker service.");
        }
    }
}