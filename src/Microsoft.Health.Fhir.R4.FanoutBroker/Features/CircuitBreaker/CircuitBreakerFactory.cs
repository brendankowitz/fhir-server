// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.CircuitBreaker
{
    /// <summary>
    /// Factory for creating and managing circuit breakers per server.
    /// </summary>
    public class CircuitBreakerFactory : ICircuitBreakerFactory
    {
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<ServerCircuitBreaker> _circuitBreakerLogger;
        private readonly ConcurrentDictionary<string, ICircuitBreaker> _circuitBreakers = new();

        public CircuitBreakerFactory(
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<ServerCircuitBreaker> circuitBreakerLogger)
        {
            _configuration = configuration ?? throw new System.ArgumentNullException(nameof(configuration));
            _circuitBreakerLogger = circuitBreakerLogger ?? throw new System.ArgumentNullException(nameof(circuitBreakerLogger));
        }

        /// <inheritdoc />
        public ICircuitBreaker GetCircuitBreaker(string serverId)
        {
            if (string.IsNullOrEmpty(serverId))
                throw new System.ArgumentException("Server ID cannot be null or empty", nameof(serverId));

            return _circuitBreakers.GetOrAdd(serverId, id =>
                new ServerCircuitBreaker(id, _configuration, _circuitBreakerLogger));
        }

        /// <inheritdoc />
        public void ResetAll()
        {
            foreach (var circuitBreaker in _circuitBreakers.Values)
            {
                circuitBreaker.Reset();
            }
        }

        /// <inheritdoc />
        public IDictionary<string, CircuitBreakerState> GetAllStates()
        {
            return _circuitBreakers.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.State);
        }
    }
}
