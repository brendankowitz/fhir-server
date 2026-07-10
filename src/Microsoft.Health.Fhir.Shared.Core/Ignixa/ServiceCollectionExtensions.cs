// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Hl7.Fhir.Serialization;
using Ignixa.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Health.Fhir.Core.Features.Search.FhirPath;
using Microsoft.Health.Fhir.Ignixa.FhirPath;

namespace Microsoft.Health.Fhir.Ignixa;

/// <summary>
/// Extension methods for registering Ignixa serialization services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Ignixa JSON serializer to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method registers <see cref="IIgnixaJsonSerializer"/>. Registration of the Ignixa
    /// <c>TextInputFormatter</c>/<c>TextOutputFormatter</c> implementations themselves is mode-gated
    /// (<see cref="Microsoft.Health.Fhir.Core.Configs.FhirSdkMode"/>) and lives entirely in
    /// <c>SdkModeFeatureModule</c> (Api layer) — those formatter types are compiled into the Api-layer
    /// assembly and are not visible from here, so this method only makes the serializer available for
    /// that module (and other consumers, e.g. persistence/validation code paths) to resolve.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddIgnixaSerialization(this IServiceCollection services)
    {
        EnsureArg.IsNotNull(services, nameof(services));

        // Register the Ignixa JSON serializer
        services.AddSingleton<IIgnixaJsonSerializer, IgnixaJsonSerializer>();

        return services;
    }

    /// <summary>
    /// Adds Ignixa FHIRPath provider to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="schemaResolver">A function that resolves the ISchema from the service provider.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method registers <see cref="IgnixaFhirPathProvider"/> as the <see cref="IFhirPathProvider"/>,
    /// replacing the default Firely-based provider. The Ignixa provider offers:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Delegate compilation for ~80% of common search patterns</description></item>
    /// <item><description>Native IElement evaluation without conversion overhead</description></item>
    /// <item><description>Full FHIRPath 2.0 specification support</description></item>
    /// <item><description>Expression caching for performance</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddIgnixaFhirPath(
        this IServiceCollection services,
        Func<IServiceProvider, ISchema> schemaResolver)
    {
        EnsureArg.IsNotNull(services, nameof(services));
        EnsureArg.IsNotNull(schemaResolver, nameof(schemaResolver));

        // Register the Ignixa FHIRPath provider, replacing any existing registration
        services.RemoveAll<IFhirPathProvider>();
        services.AddSingleton<IFhirPathProvider>(provider =>
        {
            var schema = schemaResolver(provider);
            return new IgnixaFhirPathProvider(schema);
        });

        return services;
    }
}
