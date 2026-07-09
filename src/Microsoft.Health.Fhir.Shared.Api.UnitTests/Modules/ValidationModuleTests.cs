// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Api.Modules;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Validation;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Modules
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public class ValidationModuleTests
    {
        [Fact]
        public void GivenFirelyMode_WhenLoaded_ThenModelAttributeValidatorIsUsedDirectly()
        {
            var provider = BuildProvider(FhirSdkMode.Firely);

            var validator = provider.GetRequiredService<IModelAttributeValidator>();

            Assert.IsType<ModelAttributeValidator>(validator);
        }

        [Theory]
        [InlineData(FhirSdkMode.Hybrid)]
        [InlineData(FhirSdkMode.Ignixa)]
        public void GivenHybridOrIgnixaMode_WhenLoaded_ThenIgnixaResourceValidatorWrapsIt(FhirSdkMode mode)
        {
            var provider = BuildProvider(mode);

            var validator = provider.GetRequiredService<IModelAttributeValidator>();

            Assert.IsType<IgnixaResourceValidator>(validator);
        }

        [Fact]
        public void GivenNullConfiguration_WhenConstructed_ThenThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new ValidationModule(null));
        }

        private static IServiceProvider BuildProvider(FhirSdkMode mode)
        {
            var fhirServerConfiguration = new FhirServerConfiguration();
            fhirServerConfiguration.CoreFeatures.SdkMode = mode;

            var services = new ServiceCollection();
            services.AddSingleton(Substitute.For<IIgnixaSchemaContext>());

            new ValidationModule(fhirServerConfiguration).Load(services);

            return services.BuildServiceProvider();
        }
    }
}
