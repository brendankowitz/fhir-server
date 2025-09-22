// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.FanoutBroker.Models;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.CircuitBreaker
{
    /// <summary>
    /// Circuit breaker implementation for protecting against cascade failures.
    /// </summary>
    public class CircuitBreaker : ICircuitBreaker
    {
        private readonly IOptions<FanoutBrokerConfiguration> _configuration;
        private readonly ILogger<CircuitBreaker> _logger;
        private readonly object _lock = new object();

        private CircuitBreakerState _state = CircuitBreakerState.Closed;
        private int _failureCount = 0;
        private DateTimeOffset? _lastFailureTime;
        private DateTimeOffset? _nextAttemptTime;

        public CircuitBreaker(
            string serverId,
            IOptions<FanoutBrokerConfiguration> configuration,
            ILogger<CircuitBreaker> logger)
        {
            ServerId = serverId ?? throw new ArgumentNullException(nameof(serverId));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public CircuitBreakerState State
        {
            get
            {
                lock (_lock)
                {
                    return _state;
                }
            }
        }

        /// <inheritdoc />
        public string ServerId { get; }

        /// <inheritdoc />
        public int FailureCount
        {
            get
            {
                lock (_lock)
                {
                    return _failureCount;
                }
            }
        }

        /// <inheritdoc />
        public DateTimeOffset? LastFailureTime
        {
            get
            {
                lock (_lock)
                {
                    return _lastFailureTime;
                }
            }
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            // Check if circuit breaker should allow the request
            if (!ShouldAllowRequest())
            {
                throw new CircuitBreakerOpenException(ServerId);
            }

            try
            {
                var result = await operation(cancellationToken);
                RecordSuccess();
                return result;
            }
            catch (Exception ex)
            {
                RecordFailure();
                _logger.LogWarning(ex, "Operation failed for server {ServerId}, circuit breaker failure count: {FailureCount}",
                    ServerId, FailureCount);
                throw;
            }
        }

        /// <inheritdoc />
        public void RecordSuccess()
        {
            lock (_lock)
            {
                if (_state == CircuitBreakerState.HalfOpen)
                {
                    _logger.LogInformation("Circuit breaker for server {ServerId} transitioning from HalfOpen to Closed after successful request", ServerId);
                    _state = CircuitBreakerState.Closed;
                }

                _failureCount = 0;
                _lastFailureTime = null;
                _nextAttemptTime = null;
            }
        }

        /// <inheritdoc />
        public void RecordFailure()
        {
            lock (_lock)
            {
                _failureCount++;
                _lastFailureTime = DateTimeOffset.UtcNow;

                if (_failureCount >= _configuration.Value.CircuitBreakerFailureThreshold)
                {
                    _state = CircuitBreakerState.Open;
                    _nextAttemptTime = DateTimeOffset.UtcNow.AddSeconds(_configuration.Value.CircuitBreakerTimeoutSeconds);

                    _logger.LogWarning("Circuit breaker for server {ServerId} opened after {FailureCount} failures. Next attempt at {NextAttemptTime}",
                        ServerId, _failureCount, _nextAttemptTime);
                }
            }
        }

        /// <inheritdoc />
        public void Trip()
        {
            lock (_lock)
            {
                _state = CircuitBreakerState.Open;
                _nextAttemptTime = DateTimeOffset.UtcNow.AddSeconds(_configuration.Value.CircuitBreakerTimeoutSeconds);

                _logger.LogWarning("Circuit breaker for server {ServerId} manually tripped to Open state", ServerId);
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            lock (_lock)
            {
                _state = CircuitBreakerState.Closed;
                _failureCount = 0;
                _lastFailureTime = null;
                _nextAttemptTime = null;

                _logger.LogInformation("Circuit breaker for server {ServerId} manually reset to Closed state", ServerId);
            }
        }

        private bool ShouldAllowRequest()
        {
            lock (_lock)
            {
                switch (_state)
                {
                    case CircuitBreakerState.Closed:
                        return true;

                    case CircuitBreakerState.Open:
                        if (_nextAttemptTime.HasValue && DateTimeOffset.UtcNow >= _nextAttemptTime.Value)
                        {
                            _state = CircuitBreakerState.HalfOpen;
                            _logger.LogInformation("Circuit breaker for server {ServerId} transitioning from Open to HalfOpen for test request", ServerId);
                            return true;
                        }
                        return false;

                    case CircuitBreakerState.HalfOpen:
                        // In half-open state, allow one request at a time
                        return true;

                    default:
                        return false;
                }
            }
        }
    }
}