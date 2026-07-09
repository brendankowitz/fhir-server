// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Api.Modules;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Operations.Import;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Modules
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public class OperationsModuleTests
    {
        [Fact]
        public void GivenFirelyMode_WhenLoaded_ThenImportResourceParserIsRegistered()
        {
            var provider = BuildProvider(FhirSdkMode.Firely);

            var parser = provider.GetRequiredService<IImportResourceParser>();

            Assert.IsType<ImportResourceParser>(parser);
        }

        [Theory]
        [InlineData(FhirSdkMode.Hybrid)]
        [InlineData(FhirSdkMode.Ignixa)]
        public void GivenHybridOrIgnixaMode_WhenLoaded_ThenIgnixaImportResourceParserIsRegistered(FhirSdkMode mode)
        {
            var provider = BuildProvider(mode);

            var parser = provider.GetRequiredService<IImportResourceParser>();

            Assert.IsType<IgnixaImportResourceParser>(parser);
        }

        [Fact]
        public void GivenNullConfiguration_WhenConstructed_ThenThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new OperationsModule(null));
        }

        private static IServiceProvider BuildProvider(FhirSdkMode mode)
        {
            var fhirServerConfiguration = new FhirServerConfiguration();
            fhirServerConfiguration.CoreFeatures.SdkMode = mode;

            var services = new ServiceCollection();
            services.AddSingleton(new Hl7.Fhir.Serialization.FhirJsonParser());
            services.AddSingleton(Substitute.For<IIgnixaJsonSerializer>());
            services.AddSingleton(Substitute.For<IResourceWrapperFactory>());
            services.AddSingleton(Substitute.For<IIgnixaSchemaContext>());

            new OperationsModule(fhirServerConfiguration).Load(services);

            return services.BuildServiceProvider();
        }
    }
}
