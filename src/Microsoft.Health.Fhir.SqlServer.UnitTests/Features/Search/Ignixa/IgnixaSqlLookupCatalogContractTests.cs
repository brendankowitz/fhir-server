// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search.Ignixa
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class IgnixaSqlLookupCatalogContractTests
    {
        [Fact]
        public void SystemTable_UsesExpectedLookupContract()
        {
            // VLatest exposes schema-qualified table names; assert the logical table identifier.
            Assert.Equal(
                "System",
                VLatest.System.TableName[(VLatest.System.TableName.LastIndexOf('.') + 1)..]);
            Assert.Equal("SystemId", VLatest.System.SystemId.Metadata.Name);
            Assert.Equal("Value", VLatest.System.Value.Metadata.Name);
        }

        [Fact]
        public void QuantityCodeTable_UsesExpectedLookupContract()
        {
            Assert.Equal(
                "QuantityCode",
                VLatest.QuantityCode.TableName[(VLatest.QuantityCode.TableName.LastIndexOf('.') + 1)..]);
            Assert.Equal("QuantityCodeId", VLatest.QuantityCode.QuantityCodeId.Metadata.Name);
            Assert.Equal("Value", VLatest.QuantityCode.Value.Metadata.Name);
        }
    }
}
