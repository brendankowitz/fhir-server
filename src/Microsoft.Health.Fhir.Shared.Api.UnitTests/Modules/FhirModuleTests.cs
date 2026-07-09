// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Api.Modules;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Modules
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public class FhirModuleTests
    {
        [Fact]
        public void GivenFirelyMode_WhenDeserializingJson_ThenFirelyParserIsUsed()
        {
            var provider = BuildProvider(FhirSdkMode.Firely);
            var deserializers = provider.GetRequiredService<IReadOnlyDictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>>();

            var patientJson = "{\"resourceType\":\"Patient\",\"id\":\"test\"}";
            var element = deserializers[FhirResourceFormat.Json](patientJson, "1", DateTimeOffset.UtcNow);

            Assert.Equal("Patient", element.InstanceType);

            // Firely's FhirJsonParser produces a PocoElementNode-backed ITypedElement.
            Assert.Equal("PocoElementNode", element.Instance.GetType().Name);
        }

        [Theory]
        [InlineData(FhirSdkMode.Hybrid)]
        [InlineData(FhirSdkMode.Ignixa)]
        public void GivenHybridOrIgnixaMode_WhenDeserializingJson_ThenIgnixaParserIsUsed(FhirSdkMode mode)
        {
            var provider = BuildProvider(mode);
            var deserializers = provider.GetRequiredService<IReadOnlyDictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>>();

            var patientJson = "{\"resourceType\":\"Patient\",\"id\":\"test\"}";
            var element = deserializers[FhirResourceFormat.Json](patientJson, "1", DateTimeOffset.UtcNow);

            Assert.Equal("Patient", element.InstanceType);

            // Ignixa's IgnixaResourceElement.ToTypedElement() produces a TypedElementAdapter-backed
            // ITypedElement (the Ignixa.Extensions.FirelySdk shim), distinct from Firely's PocoElementNode.
            Assert.Equal("TypedElementAdapter", element.Instance.GetType().Name);
        }

        [Fact]
        public void GivenNullConfiguration_WhenConstructed_ThenThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new FhirModule(null));
        }

        [Fact]
        public void GivenFirelyMode_WhenSearchModuleAlsoLoaded_ThenFirelyFhirPathProviderIsRegistered()
        {
            var provider = BuildProviderWithSearchModule(FhirSdkMode.Firely);

            var fhirPathProvider = provider.GetRequiredService<Microsoft.Health.Fhir.Core.Features.Search.FhirPath.IFhirPathProvider>();

            Assert.IsType<Microsoft.Health.Fhir.Core.Features.Search.FhirPath.FirelyFhirPathProvider>(fhirPathProvider);
        }

        [Theory]
        [InlineData(FhirSdkMode.Hybrid)]
        [InlineData(FhirSdkMode.Ignixa)]
        public void GivenHybridOrIgnixaMode_WhenSearchModuleAlsoLoaded_ThenIgnixaFhirPathProviderIsRegistered(FhirSdkMode mode)
        {
            var provider = BuildProviderWithSearchModule(mode);

            var fhirPathProvider = provider.GetRequiredService<Microsoft.Health.Fhir.Core.Features.Search.FhirPath.IFhirPathProvider>();

            Assert.IsType<Microsoft.Health.Fhir.Ignixa.FhirPath.IgnixaFhirPathProvider>(fhirPathProvider);
        }

        private static IServiceProvider BuildProvider(FhirSdkMode mode)
        {
            var fhirServerConfiguration = new FhirServerConfiguration();
            fhirServerConfiguration.CoreFeatures.SdkMode = mode;

            var services = new ServiceCollection();

            new FhirModule(fhirServerConfiguration).Load(services);

            return services.BuildServiceProvider();
        }

        private static IServiceProvider BuildProviderWithSearchModule(FhirSdkMode mode)
        {
            var fhirServerConfiguration = new FhirServerConfiguration();
            fhirServerConfiguration.CoreFeatures.SdkMode = mode;

            var services = new ServiceCollection();

            new FhirModule(fhirServerConfiguration).Load(services);
            new Microsoft.Health.Fhir.Api.Modules.SearchModule(fhirServerConfiguration).Load(services);

            return services.BuildServiceProvider();
        }
    }
}
