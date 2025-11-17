// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Text.Json.Serialization;

namespace Microsoft.Health.Fhir.FanoutBroker.Models
{
    /// <summary>
    /// Represents a continuation token for a specific FHIR server.
    /// </summary>
    public class ServerContinuationToken
    {
        /// <summary>
        /// Server endpoint identifier.
        /// </summary>
        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; }

        /// <summary>
        /// Server-specific continuation token.
        /// </summary>
        [JsonPropertyName("token")]
        public string Token { get; set; }

        /// <summary>
        /// Whether this server has been exhausted (no more results).
        /// </summary>
        [JsonPropertyName("exhausted")]
        public bool Exhausted { get; set; }

        /// <summary>
        /// Last sort value received from this server (for sorting continuation).
        /// </summary>
        [JsonPropertyName("last_sort_value")]
        public string LastSortValue { get; set; }

        /// <summary>
        /// Number of results returned from this server in the current page.
        /// </summary>
        [JsonPropertyName("results_returned")]
        public int ResultsReturned { get; set; }
    }
}
