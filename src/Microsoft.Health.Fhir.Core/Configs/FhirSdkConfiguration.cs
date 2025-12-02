// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Configs
{
    /// <summary>
    /// Configuration for FHIR SDK selection and behavior.
    /// </summary>
    public class FhirSdkConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether to use Ignixa SDK instead of Firely SDK.
        /// Default is false (use Firely SDK).
        /// This is a feature flag for Phase 2 of the Ignixa SDK migration (ADR-2512).
        /// </summary>
        public bool UseIgnixaSdk { get; set; } = false;
    }
}
