// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using EnsureThat;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.Everything;
using Microsoft.Health.Fhir.Core.Features.Operations.Export;
using Microsoft.Health.Fhir.Core.Features.Operations.Import;
using Microsoft.Health.Fhir.Core.Features.Operations.Reindex;
using Microsoft.Health.Fhir.Core.Messages.Storage;
using Microsoft.Health.Fhir.Shared.Core.Features.Operations.Import;

namespace Microsoft.Health.Fhir.Api.Modules
{
    /// <summary>
    /// Registration of operations components.
    /// </summary>
    public class OperationsModule : IStartupModule
    {
        private readonly FhirSdkMode _sdkMode;

        public OperationsModule(FhirServerConfiguration fhirServerConfiguration)
        {
            EnsureArg.IsNotNull(fhirServerConfiguration, nameof(fhirServerConfiguration));
            _sdkMode = fhirServerConfiguration.CoreFeatures.SdkMode;
        }

        public void Load(IServiceCollection services)
        {
            EnsureArg.IsNotNull(services, nameof(services));

            services.Add<GroupMemberExtractor>()
                .Singleton()
                .AsService<IGroupMemberExtractor>();

            services.Add<ExportJobTask>()
                .Transient()
                .AsSelf();

            services.Add<IExportJobTask>(sp => sp.GetRequiredService<ExportJobTask>())
                .Transient()
                .AsSelf()
                .AsFactory();

            services.Add<ResourceToNdjsonBytesSerializer>()
                .Singleton()
                .AsService<IResourceToByteArraySerializer>();

            services.AddSingleton<IPatientEverythingService, PatientEverythingService>();

            services.Add<ImportResourceLoader>()
                .Transient()
                .AsSelf()
                .AsImplementedInterfaces();

            if (_sdkMode == FhirSdkMode.Firely)
            {
                services.Add<FirelyImportResourceParser>()
                    .Transient()
                    .AsSelf()
                    .AsService<IImportResourceParser>();
            }
            else
            {
                services.Add<ImportResourceParser>()
                    .Transient()
                    .AsSelf()
                    .AsService<IImportResourceParser>();
            }

            services.Add<ImportErrorStoreFactory>()
                .Transient()
                .AsSelf()
                .AsImplementedInterfaces();

            services.Add<ImportErrorSerializer>()
                .Transient()
                .AsSelf()
                .AsImplementedInterfaces();

            services.Add<AzureAccessTokenProvider>()
                .Transient()
                .AsService<IAccessTokenProvider>();
        }
    }
}
