// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.FanoutBroker.Extensions
{
    /// <summary>
    /// Extension methods for configuring the application pipeline for FHIR services.
    /// </summary>
    public static class ApplicationBuilderExtensions
    {
        /// <summary>
        /// Adds FHIR server middleware to the application pipeline.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <returns>Application builder for chaining.</returns>
        public static Microsoft.AspNetCore.Builder.IApplicationBuilder UseFhirServer(
            this Microsoft.AspNetCore.Builder.IApplicationBuilder app)
        {
            // In a real implementation, this would add FHIR-specific middleware
            // For now, this is a placeholder
            return app;
        }
    }
}
