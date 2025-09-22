// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.CircuitBreaker
{
    /// <summary>
    /// Exception thrown when a circuit breaker is open and blocking requests.
    /// </summary>
    public class CircuitBreakerOpenException : Exception
    {
        public CircuitBreakerOpenException(string serverId)
            : base($"Circuit breaker is open for server '{serverId}'")
        {
            ServerId = serverId;
        }

        public CircuitBreakerOpenException(string serverId, string message)
            : base(message)
        {
            ServerId = serverId;
        }

        public CircuitBreakerOpenException(string serverId, string message, Exception innerException)
            : base(message, innerException)
        {
            ServerId = serverId;
        }

        /// <summary>
        /// Gets the server identifier for which the circuit breaker is open.
        /// </summary>
        public string ServerId { get; }
    }
}