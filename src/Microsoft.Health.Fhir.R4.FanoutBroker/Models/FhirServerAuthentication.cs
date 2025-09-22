// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.FanoutBroker.Models
{
    /// <summary>
    /// Authentication configuration for a FHIR server endpoint.
    /// </summary>
    public class FhirServerAuthentication
    {
        /// <summary>
        /// Authentication type (None, Bearer, Basic, ClientCredentials).
        /// </summary>
        public AuthenticationType Type { get; set; } = AuthenticationType.None;

        /// <summary>
        /// Bearer token for Bearer authentication.
        /// </summary>
        public string BearerToken { get; set; }

        /// <summary>
        /// Username for Basic authentication.
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Password for Basic authentication.
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// Client ID for OAuth2 client credentials flow.
        /// </summary>
        public string ClientId { get; set; }

        /// <summary>
        /// Client secret for OAuth2 client credentials flow.
        /// </summary>
        public string ClientSecret { get; set; }

        /// <summary>
        /// Token endpoint for OAuth2 client credentials flow.
        /// </summary>
        public string TokenEndpoint { get; set; }

        /// <summary>
        /// Scope for OAuth2 client credentials flow.
        /// </summary>
        public string Scope { get; set; }
    }
}