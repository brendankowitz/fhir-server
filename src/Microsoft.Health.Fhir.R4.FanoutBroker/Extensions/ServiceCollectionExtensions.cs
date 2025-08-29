// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.FanoutBroker.Features.Health;

namespace Microsoft.Health.Fhir.FanoutBroker.Extensions
{
    /// <summary>
    /// Extension methods for configuring FHIR services in the fanout broker.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds basic FHIR server services configuration.
        /// This is a placeholder for FHIR server registration.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>Service collection for chaining.</returns>
        public static IServiceCollection AddFhirServer(this IServiceCollection services)
        {
            // In a real implementation, this would add the core FHIR server services
            // For now, this is a placeholder
            return services;
        }

        /// <summary>
        /// Adds a search service implementation.
        /// </summary>
        /// <typeparam name="TSearchService">The search service implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>Service collection for chaining.</returns>
        public static IServiceCollection AddSearchService<TSearchService>(this IServiceCollection services)
            where TSearchService : class
        {
            // In a real implementation, this would properly register the search service
            // For now, this is a placeholder
            return services;
        }

        /// <summary>
        /// Adds a capability provider implementation.
        /// </summary>
        /// <typeparam name="TCapabilityProvider">The capability provider implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>Service collection for chaining.</returns>
        public static IServiceCollection AddCapabilityProvider<TCapabilityProvider>(this IServiceCollection services)
            where TCapabilityProvider : class
        {
            // In a real implementation, this would properly register the capability provider
            // For now, this is a placeholder
            return services;
        }
    }

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