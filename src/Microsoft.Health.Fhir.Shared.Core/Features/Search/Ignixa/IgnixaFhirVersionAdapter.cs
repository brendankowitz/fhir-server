// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Maps the compiled FHIR version to the corresponding Ignixa version.
    /// </summary>
    public static class IgnixaFhirVersionAdapter
    {
        /// <summary>
        /// Gets the Ignixa FHIR version for the active compilation symbol.
        /// </summary>
        public static Ignixa.Abstractions.FhirVersion Current
        {
            get
            {
#if Stu3
                return Ignixa.Abstractions.FhirVersion.Stu3;
#elif R4B
                return Ignixa.Abstractions.FhirVersion.R4B;
#elif R4
                return Ignixa.Abstractions.FhirVersion.R4;
#elif R5
                return Ignixa.Abstractions.FhirVersion.R5;
#else
                throw new InvalidOperationException("No FHIR version compilation symbol is configured.");
#endif
            }
        }
    }
}
