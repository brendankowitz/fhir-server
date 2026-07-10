// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using EnsureThat;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Api.Features.Formatters;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Ignixa;

namespace Microsoft.Health.Fhir.Api.Modules.FeatureFlags.SdkMode
{
    /// <summary>
    /// Registers the JSON <see cref="TextInputFormatter"/>/<see cref="TextOutputFormatter"/>
    /// implementations that participate in MVC's formatter negotiation, per <see cref="FhirSdkMode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single place that decides which JSON formatter family (or both) is registered.
    /// Registration ORDER within <see cref="Load"/> is significant: <c>FormatterConfiguration</c>
    /// preserves DI registration order when it inserts same-media-type formatters into
    /// <c>MvcOptions</c>, so registering Ignixa before Firely is what makes Ignixa win in
    /// <see cref="FhirSdkMode.Hybrid"/> mode. A single combined module (rather than one module per
    /// SDK) keeps that ordering explicit and local instead of depending on cross-module load order.
    /// </para>
    /// <para>
    /// Do not register both families unconditionally: in <see cref="FhirSdkMode.Firely"/> mode,
    /// registering Ignixa at all would leave it competing for <c>application/json</c>, reproducing
    /// the formatter-selection defect this module exists to fix.
    /// </para>
    /// </remarks>
    public class SdkModeFeatureModule : IStartupModule
    {
        private readonly FhirSdkMode _sdkMode;

        public SdkModeFeatureModule(FhirServerConfiguration fhirServerConfiguration)
        {
            EnsureArg.IsNotNull(fhirServerConfiguration, nameof(fhirServerConfiguration));
            _sdkMode = fhirServerConfiguration.CoreFeatures.SdkMode;
        }

        /// <inheritdoc />
        public void Load(IServiceCollection services)
        {
            EnsureArg.IsNotNull(services, nameof(services));

            // Registration ORDER within this method determines formatter precedence for JSON media types.
            switch (_sdkMode)
            {
                case FhirSdkMode.Ignixa:
                    RegisterIgnixaJsonFormatters(services);
                    break;
                case FhirSdkMode.Firely:
                    RegisterFirelyJsonFormatters(services);
                    break;
                case FhirSdkMode.Hybrid:
                default:
                    RegisterIgnixaJsonFormatters(services); // registered first -> wins ties
                    RegisterFirelyJsonFormatters(services); // fallback, still selectable via CanRead/CanWrite
                    break;
            }
        }

        private static void RegisterIgnixaJsonFormatters(IServiceCollection services)
        {
            services.Add<IgnixaFhirJsonInputFormatter>()
                .Singleton()
                .AsSelf()
                .AsService<TextInputFormatter>();

            services.Add<IgnixaFhirJsonOutputFormatter>()
                .Singleton()
                .AsSelf()
                .AsService<TextOutputFormatter>();
        }

        private static void RegisterFirelyJsonFormatters(IServiceCollection services)
        {
            services.Add<FhirJsonInputFormatter>()
                .Singleton()
                .AsSelf()
                .AsService<TextInputFormatter>();

            services.Add<FhirJsonOutputFormatter>()
                .Singleton()
                .AsSelf()
                .AsService<TextOutputFormatter>();
        }
    }
}
