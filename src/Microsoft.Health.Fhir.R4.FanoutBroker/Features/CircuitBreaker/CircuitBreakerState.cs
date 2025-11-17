// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.FanoutBroker.Features.CircuitBreaker
{
    /// <summary>
    /// Circuit breaker state enumeration.
    /// </summary>
    public enum CircuitBreakerState
    {
        /// <summary>
        /// Circuit is closed, requests are allowed through.
        /// </summary>
        Closed,

        /// <summary>
        /// Circuit is open, requests are blocked.
        /// </summary>
        Open,

        /// <summary>
        /// Circuit is half-open, allowing a limited number of test requests.
        /// </summary>
        HalfOpen,
    }
}
