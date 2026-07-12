// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Api.Modules;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Modules
{
    /// <summary>
    /// Guards the <see cref="SearchModule"/> mode-selection seam that Phase 4 Plan A (US-15) introduced:
    /// Hybrid/Ignixa mode must register <see cref="IgnixaBundleFactory"/> as the <see cref="IBundleFactory"/>,
    /// and Firely mode must keep <see cref="BundleFactory"/>. The rest of the module's registrations are not
    /// exercised here -- only the one decision that routes every search/history bundle to a given assembler.
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public class SearchModuleTests
    {
        [Fact]
        public void GivenFirelyMode_WhenLoaded_ThenBundleFactoryIsRegistered()
        {
            Type implementationType = GetBundleFactoryImplementationType(FhirSdkMode.Firely);

            Assert.Equal(typeof(BundleFactory), implementationType);
        }

        [Theory]
        [InlineData(FhirSdkMode.Hybrid)]
        [InlineData(FhirSdkMode.Ignixa)]
        public void GivenHybridOrIgnixaMode_WhenLoaded_ThenIgnixaBundleFactoryIsRegistered(FhirSdkMode mode)
        {
            Type implementationType = GetBundleFactoryImplementationType(mode);

            Assert.Equal(typeof(IgnixaBundleFactory), implementationType);
        }

        [Fact]
        public void GivenNullConfiguration_WhenConstructed_ThenThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new SearchModule(null));
        }

        private static Type GetBundleFactoryImplementationType(FhirSdkMode mode)
        {
            var fhirServerConfiguration = new FhirServerConfiguration();
            fhirServerConfiguration.CoreFeatures.SdkMode = mode;

            var services = new ServiceCollection();
            new SearchModule(fhirServerConfiguration).Load(services);

            ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(IBundleFactory));
            return descriptor.ImplementationType;
        }
    }
}
