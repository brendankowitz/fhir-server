// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Features.CircuitBreaker;
using Microsoft.Health.Fhir.FanoutBroker.Features.Configuration;
using Microsoft.Health.Fhir.FanoutBroker.Features.Conformance;
using Microsoft.Health.Fhir.FanoutBroker.Features.Health;
using Microsoft.Health.Fhir.FanoutBroker.Features.Protection;
using Microsoft.Health.Fhir.FanoutBroker.Features.QueryOptimization;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search;
using Microsoft.Health.Fhir.FanoutBroker.Features.Search.Sorting;

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

        /// <summary>
        /// Adds all fanout broker services and components to the service collection.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>Service collection for chaining.</returns>
        public static IServiceCollection AddFanoutBrokerServices(this IServiceCollection services)
        {
            // Core fanout services
            services.AddScoped<IExecutionStrategyAnalyzer, ExecutionStrategyAnalyzer>();
            services.AddScoped<IFhirServerOrchestrator, FhirServerOrchestrator>();
            services.AddScoped<IResultAggregator, ResultAggregator>();
            services.AddScoped<IChainedSearchProcessor, ChainedSearchProcessor>();
            services.AddScoped<IIncludeProcessor, IncludeProcessor>();
            services.AddScoped<IFanoutCapabilityStatementProvider, FanoutCapabilityStatementProvider>();
            services.AddScoped<ISearchService, FanoutSearchService>();
            services.AddScoped<IConfigurationValidationService, ConfigurationValidationService>();

            // Resource protection and optimization
            services.AddScoped<IResourceProtectionService, ResourceProtectionService>();
            services.AddScoped<IQueryOptimizationService, SimpleQueryOptimizationService>();

            // Circuit breaker services (singleton for shared state)
            services.AddSingleton<ICircuitBreakerFactory, CircuitBreakerFactory>();

            // Distributed sorting service
            services.AddScoped<IDistributedSortingService, DistributedSortingService>();

            // Expression-based resolution strategy services
            services.AddScoped<IExpressionResolutionStrategyFactory, ExpressionResolutionStrategyFactory>();
            services.AddScoped<ExpressionDistributedResolutionStrategy>();
            services.AddScoped<ExpressionPassthroughResolutionStrategy>();

            return services;
        }
    }
}
