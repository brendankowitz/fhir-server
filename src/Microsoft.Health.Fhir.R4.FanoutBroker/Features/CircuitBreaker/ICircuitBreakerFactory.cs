// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.FanoutBroker.Features.CircuitBreaker
{
    /// <summary>
    /// Factory for creating and managing circuit breakers.
    /// </summary>
    public interface ICircuitBreakerFactory
    {
        /// <summary>
        /// Gets or creates a circuit breaker for the specified server.
        /// </summary>
        /// <param name="serverId">The server identifier.</param>
        /// <returns>A circuit breaker instance for the server.</returns>
        ICircuitBreaker GetCircuitBreaker(string serverId);

        /// <summary>
        /// Resets all circuit breakers to the closed state.
        /// </summary>
        void ResetAll();

        /// <summary>
        /// Gets the circuit breaker states for all servers.
        /// </summary>
        /// <returns>Dictionary of server IDs to their circuit breaker states.</returns>
        System.Collections.Generic.IDictionary<string, CircuitBreakerState> GetAllStates();
    }
}