// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Health.Fhir.FanoutBroker.Models
{
    /// <summary>
    /// Represents an aggregated continuation token that combines continuation tokens from multiple FHIR servers.
    /// </summary>
    public class FanoutContinuationToken
    {
        /// <summary>
        /// Per-server continuation tokens.
        /// </summary>
        [JsonPropertyName("servers")]
        public List<ServerContinuationToken> Servers { get; set; } = new List<ServerContinuationToken>();

        /// <summary>
        /// Sort criteria used for the original query.
        /// </summary>
        [JsonPropertyName("sort_criteria")]
        public string SortCriteria { get; set; }

        /// <summary>
        /// Page size for the original query.
        /// </summary>
        [JsonPropertyName("page_size")]
        public int PageSize { get; set; }

        /// <summary>
        /// Execution strategy used for the original query.
        /// </summary>
        [JsonPropertyName("execution_strategy")]
        public string ExecutionStrategy { get; set; }

        /// <summary>
        /// Resource type being searched (for optimization).
        /// </summary>
        [JsonPropertyName("resource_type")]
        public string ResourceType { get; set; }

        /// <summary>
        /// Serializes the token to JSON string.
        /// </summary>
        /// <returns>JSON representation of the continuation token.</returns>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
        }

        /// <summary>
        /// Deserializes a JSON string to a FanoutContinuationToken.
        /// </summary>
        /// <param name="json">JSON string representation.</param>
        /// <returns>Deserialized FanoutContinuationToken or null if invalid.</returns>
        public static FanoutContinuationToken FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<FanoutContinuationToken>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a base-64 encoded token string.
        /// </summary>
        /// <returns>Base-64 encoded token string.</returns>
        public string ToBase64String()
        {
            var json = ToJson();
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            return System.Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Deserializes a base-64 encoded token string.
        /// </summary>
        /// <param name="base64Token">Base-64 encoded token string.</param>
        /// <returns>Deserialized FanoutContinuationToken or null if invalid.</returns>
        public static FanoutContinuationToken FromBase64String(string base64Token)
        {
            if (string.IsNullOrEmpty(base64Token))
                return null;

            try
            {
                var bytes = System.Convert.FromBase64String(base64Token);
                var json = System.Text.Encoding.UTF8.GetString(bytes);
                return FromJson(json);
            }
            catch
            {
                return null;
            }
        }
    }

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