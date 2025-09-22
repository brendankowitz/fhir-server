// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.ContinuationToken
{
    /// <summary>
    /// Distributed continuation token for managing pagination across multiple FHIR servers.
    /// </summary>
    public class DistributedContinuationToken
    {
        /// <summary>
        /// Per-server continuation tokens.
        /// </summary>
        [JsonPropertyName("servers")]
        public List<ServerContinuationToken> Servers { get; set; } = new List<ServerContinuationToken>();

        /// <summary>
        /// Sort criteria used for this query (e.g., "_lastUpdated,desc").
        /// </summary>
        [JsonPropertyName("sort_criteria")]
        public string SortCriteria { get; set; }

        /// <summary>
        /// Page size for this query.
        /// </summary>
        [JsonPropertyName("page_size")]
        public int PageSize { get; set; }

        /// <summary>
        /// Last sort values for each sort parameter to enable proper continuation.
        /// </summary>
        [JsonPropertyName("last_sort_values")]
        public Dictionary<string, object> LastSortValues { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Timestamp when this token was created.
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Version of the continuation token format for backward compatibility.
        /// </summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>
        /// Serializes the token to a base64-encoded string.
        /// </summary>
        public string Serialize()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
                return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to serialize distributed continuation token", ex);
            }
        }

        /// <summary>
        /// Deserializes a distributed continuation token from a base64-encoded string.
        /// </summary>
        public static DistributedContinuationToken Deserialize(string token)
        {
            if (string.IsNullOrEmpty(token))
                throw new ArgumentException("Token cannot be null or empty", nameof(token));

            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
                return JsonSerializer.Deserialize<DistributedContinuationToken>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to deserialize distributed continuation token", ex);
            }
        }

        /// <summary>
        /// Checks if the token has expired based on a configurable timeout.
        /// </summary>
        public bool IsExpired(TimeSpan timeout)
        {
            return DateTimeOffset.UtcNow - CreatedAt > timeout;
        }

        /// <summary>
        /// Gets the continuation token for a specific server.
        /// </summary>
        public string GetServerToken(string serverId)
        {
            return Servers.FirstOrDefault(s => s.Endpoint == serverId)?.Token;
        }

        /// <summary>
        /// Updates the continuation token for a specific server.
        /// </summary>
        public void UpdateServerToken(string serverId, string token)
        {
            var serverToken = Servers.FirstOrDefault(s => s.Endpoint == serverId);
            if (serverToken != null)
            {
                serverToken.Token = token;
            }
            else
            {
                Servers.Add(new ServerContinuationToken { Endpoint = serverId, Token = token });
            }
        }
    }

    /// <summary>
    /// Continuation token for a specific server.
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
    }
}