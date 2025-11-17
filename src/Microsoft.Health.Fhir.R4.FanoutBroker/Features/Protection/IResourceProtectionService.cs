// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Search;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Protection
{
    /// <summary>
    /// Service for enforcing resource limits and protecting against resource exhaustion attacks.
    /// </summary>
    public interface IResourceProtectionService
    {
        /// <summary>
        /// Validates that a search request complies with configured resource limits.
        /// </summary>
        /// <param name="searchOptions">The search options to validate.</param>
        /// <param name="clientId">Optional client identifier for rate limiting.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if the request is allowed, false if it should be rejected.</returns>
        Task<ResourceProtectionResult> ValidateSearchRequestAsync(
            SearchOptions searchOptions,
            string clientId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if the current memory usage is within acceptable limits.
        /// </summary>
        /// <returns>True if memory usage is acceptable, false if it's too high.</returns>
        bool IsMemoryUsageAcceptable();

        /// <summary>
        /// Validates that the result count is within configured limits.
        /// </summary>
        /// <param name="currentResultCount">Current number of results.</param>
        /// <param name="estimatedAdditionalResults">Estimated additional results.</param>
        /// <returns>True if the total would be within limits.</returns>
        bool ValidateResultCount(int currentResultCount, int estimatedAdditionalResults = 0);

        /// <summary>
        /// Checks if a resource size is within acceptable limits.
        /// </summary>
        /// <param name="resourceSizeBytes">Size of the resource in bytes.</param>
        /// <returns>True if the resource size is acceptable.</returns>
        bool ValidateResourceSize(long resourceSizeBytes);

        /// <summary>
        /// Records the start of a search operation for concurrency tracking.
        /// </summary>
        /// <returns>A token representing the search operation, or null if rejected due to concurrency limits.</returns>
        Task<SearchOperationToken> BeginSearchOperationAsync();

        /// <summary>
        /// Records the completion of a search operation.
        /// </summary>
        /// <param name="token">The token from BeginSearchOperationAsync.</param>
        void EndSearchOperation(SearchOperationToken token);
    }

    /// <summary>
    /// Result of resource protection validation.
    /// </summary>
    public class ResourceProtectionResult
    {
        /// <summary>
        /// Whether the request is allowed.
        /// </summary>
        public bool IsAllowed { get; set; }

        /// <summary>
        /// Reason for rejection if not allowed.
        /// </summary>
        public string RejectionReason { get; set; }

        /// <summary>
        /// HTTP status code to return if rejected.
        /// </summary>
        public int? SuggestedStatusCode { get; set; }
    }

    /// <summary>
    /// Token representing an active search operation for concurrency tracking.
    /// </summary>
    public class SearchOperationToken
    {
        /// <summary>
        /// Unique identifier for this search operation.
        /// </summary>
        public string OperationId { get; set; } = System.Guid.NewGuid().ToString();

        /// <summary>
        /// Timestamp when the operation started.
        /// </summary>
        public System.DateTimeOffset StartTime { get; set; } = System.DateTimeOffset.UtcNow;
    }
}