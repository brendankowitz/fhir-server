// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Configs
{
    /// <summary>
    /// Controls which FHIR SDK (Firely or Ignixa) serves each request, at the level of granularity
    /// currently supported by the server. See <see cref="CoreFeatureConfiguration.SdkMode"/>.
    /// </summary>
    public enum FhirSdkMode
    {
        /// <summary>
        /// Ignixa is preferred where wired up; Firely remains registered as a fallback. Default mode.
        /// </summary>
        Hybrid = 0,

        /// <summary>
        /// Forces Firely wherever a mode-gated seam exists.
        /// </summary>
        Firely = 1,

        /// <summary>
        /// Forces Ignixa wherever a mode-gated seam exists.
        /// </summary>
        Ignixa = 2,
    }
}
