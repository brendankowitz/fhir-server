// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Data;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.SqlServer.Features.Schema.Model;
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
            Assert.Equal("dbo.System", VLatest.System.TableName);
            AssertColumnFacts(VLatest.System.SystemId, "SystemId", SqlDbType.Int, null, false);
            AssertStringColumnFacts(VLatest.System.Value, "Value", 256, false, null, null, null);
        }

        [Fact]
        public void QuantityCodeTable_UsesExpectedLookupContract()
        {
            Assert.Equal("dbo.QuantityCode", VLatest.QuantityCode.TableName);
            AssertColumnFacts(VLatest.QuantityCode.QuantityCodeId, "QuantityCodeId", SqlDbType.Int, null, false);
            AssertStringColumnFacts(
                VLatest.QuantityCode.Value,
                "Value",
                256,
                false,
                "Latin1_General_100_CS_AS",
                true,
                true);
        }

        private static void AssertColumnFacts(
            Column column,
            string expectedName,
            SqlDbType expectedSqlType,
            long? expectedMaxLength,
            bool expectedNullable)
        {
            Assert.Equal(expectedName, column.Metadata.Name);
            Assert.Equal(expectedSqlType, column.Metadata.SqlDbType);
            Assert.Equal(expectedMaxLength, GetSchemaMaxLength(column));
            Assert.Equal(expectedNullable, column.Nullable);
        }

        private static void AssertStringColumnFacts(
            StringColumn column,
            string expectedName,
            long expectedMaxLength,
            bool expectedNullable,
            string expectedCollation,
            bool? expectedIsCaseSensitive,
            bool? expectedIsAcentSensitive)
        {
            AssertColumnFacts(
                column,
                expectedName,
                SqlDbType.NVarChar,
                expectedMaxLength,
                expectedNullable);
            Assert.Equal(expectedCollation, column.Collation);
            Assert.Equal(expectedIsCaseSensitive, column.IsCaseSensitive);
            Assert.Equal(expectedIsAcentSensitive, column.IsAcentSensitive);
        }

        private static long? GetSchemaMaxLength(Column column)
        {
            // SqlMetaData.MaxLength is byte storage size for fixed-width types, while the schema
            // manifest's max length is character length and is not applicable to those types.
            return column.Metadata.SqlDbType == SqlDbType.NVarChar
                ? column.Metadata.MaxLength
                : null;
        }
    }
}
