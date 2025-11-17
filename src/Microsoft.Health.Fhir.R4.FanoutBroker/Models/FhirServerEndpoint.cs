// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.Health.Fhir.FanoutBroker.Models
{
    /// <summary>
    /// Configuration for a single FHIR server endpoint.
    /// </summary>
    public class FhirServerEndpoint
    {
        /// <summary>
        /// Unique identifier for this server endpoint.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Display name for this server endpoint.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Base URL of the FHIR server.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1056:UriPropertiesShouldNotBeStrings", Justification = "Configuration property needs string for JSON deserialization")]
        public string BaseUrl { get; set; }

        /// <summary>
        /// Authentication configuration for this server.
        /// </summary>
        public FhirServerAuthentication Authentication { get; set; }

        /// <summary>
        /// Whether this server is enabled for queries.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Priority weight for this server (higher priority servers are queried first in sequential execution).
        /// </summary>
        public int Priority { get; set; } = 1;

        /// <summary>
        /// Timeout specific to this server in seconds (overrides global timeout if set).
        /// </summary>
        public int? TimeoutSeconds { get; set; }

        /// <summary>
        /// Additional HTTP headers to send to this server.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists", Justification = "Configuration class requires Dictionary<T,T> for JSON deserialization")]
        [SuppressMessage("Microsoft.Design", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Configuration class requires setter for JSON deserialization")]
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    }
}