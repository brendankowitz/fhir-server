// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.CircuitBreaker
{
    /// <summary>
    /// Interface for circuit breaker pattern implementation.
    /// </summary>
    public interface ICircuitBreaker
    {
        /// <summary>
        /// Gets the current state of the circuit breaker.
        /// </summary>
        CircuitBreakerState State { get; }

        /// <summary>
        /// Gets the server identifier this circuit breaker protects.
        /// </summary>
        string ServerId { get; }

        /// <summary>
        /// Gets the current failure count.
        /// </summary>
        int FailureCount { get; }

        /// <summary>
        /// Gets the last failure time.
        /// </summary>
        DateTimeOffset? LastFailureTime { get; }

        /// <summary>
        /// Executes an operation through the circuit breaker.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        /// <exception cref="CircuitBreakerOpenException">Thrown when the circuit is open.</exception>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);

        /// <summary>
        /// Records a successful operation.
        /// </summary>
        void RecordSuccess();

        /// <summary>
        /// Records a failed operation.
        /// </summary>
        void RecordFailure();

        /// <summary>
        /// Manually trips the circuit breaker to the open state.
        /// </summary>
        void Trip();

        /// <summary>
        /// Manually resets the circuit breaker to the closed state.
        /// </summary>
        void Reset();
    }
}