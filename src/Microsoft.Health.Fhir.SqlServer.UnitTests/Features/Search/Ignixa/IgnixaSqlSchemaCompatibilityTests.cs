// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Ignixa.Search.Sql.Catalog;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search.Ignixa
{
    /// <summary>
    /// Verifies that the Ignixa SQL compiler catalog (<see cref="SqlCatalog.Default"/>) is compatible
    /// with the FHIR Server schema 116 search-index contract. Each test asserts table/column presence
    /// and exact SQL type, max length, collation, and nullability facts as exposed by
    /// <see cref="ColumnDescriptor"/>. Failures identify the specific table/column and expected vs actual facts.
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class IgnixaSqlSchemaCompatibilityTests
    {
        private readonly SqlCatalog _catalog = SqlCatalog.Default;

        /// <summary>
        /// Schema 116 manifest: tables and columns the compiler must know about.
        /// Derived from VLatest.Generated.net10.0.cs (schema version 116).
        /// </summary>
        private static readonly IReadOnlyList<ExpectedTable> Schema116Manifest = new[]
        {
            new ExpectedTable("Resource", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceId", "varchar", 64, "Latin1_General_100_CS_AS", false),
                Col("Version", "int", null, null, false),
                Col("IsHistory", "bit", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("IsDeleted", "bit", null, null, false),
                Col("RequestMethod", "varchar", 10, null, true),
                Col("RawResource", "varbinary", null, null, false),
                Col("IsRawResourceMetaSet", "bit", null, null, false),
                Col("SearchParamHash", "varchar", 64, null, true),
                Col("TransactionId", "bigint", null, null, true),
                Col("HistoryTransactionId", "bigint", null, null, true),
            }),
            new ExpectedTable("ResourceType", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("Name", "nvarchar", 50, "Latin1_General_100_CS_AS", false),
            }),
            new ExpectedTable("SearchParam", "dbo", new[]
            {
                Col("SearchParamId", "smallint", null, null, false),
                Col("Uri", "varchar", 128, "Latin1_General_100_CS_AS", false),
                Col("Status", "varchar", 20, null, false),
                Col("LastUpdated", "datetimeoffset", 7, null, false),
                Col("IsPartiallySupported", "bit", null, null, false),
            }),
            new ExpectedTable("System", "dbo", new[]
            {
                Col("SystemId", "int", null, null, false),
                Col("Value", "nvarchar", 256, null, false),
            }),
            new ExpectedTable("QuantityCode", "dbo", new[]
            {
                Col("QuantityCodeId", "int", null, null, false),
                Col("Value", "nvarchar", 256, "Latin1_General_100_CS_AS", false),
            }),
            new ExpectedTable("StringSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("Text", "nvarchar", 256, "Latin1_General_100_CI_AI_SC", false),
                Col("TextOverflow", "nvarchar", null, "Latin1_General_100_CI_AI_SC", true),
                Col("IsMin", "bit", null, null, false),
                Col("IsMax", "bit", null, null, false),
            }),
            new ExpectedTable("TokenSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("SystemId", "int", null, null, true),
                Col("Code", "varchar", 256, "Latin1_General_100_CS_AS", false),
                Col("CodeOverflow", "varchar", null, "Latin1_General_100_CS_AS", true),
            }),
            new ExpectedTable("DateTimeSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("StartDateTime", "datetime2", 7, null, false),
                Col("EndDateTime", "datetime2", 7, null, false),
                Col("IsLongerThanADay", "bit", null, null, false),
                Col("IsMin", "bit", null, null, false),
                Col("IsMax", "bit", null, null, false),
            }),
            new ExpectedTable("NumberSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("SingleValue", "decimal", 36, null, true),
                Col("LowValue", "decimal", 36, null, false),
                Col("HighValue", "decimal", 36, null, false),
            }),
            new ExpectedTable("QuantitySearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("SystemId", "int", null, null, true),
                Col("QuantityCodeId", "int", null, null, true),
                Col("SingleValue", "decimal", 36, null, true),
                Col("LowValue", "decimal", 36, null, false),
                Col("HighValue", "decimal", 36, null, false),
            }),
            new ExpectedTable("ReferenceSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("BaseUri", "varchar", 128, "Latin1_General_100_CS_AS", true),
                Col("ReferenceResourceTypeId", "smallint", null, null, true),
                Col("ReferenceResourceId", "varchar", 64, "Latin1_General_100_CS_AS", false),
                Col("ReferenceResourceVersion", "int", null, null, true),
            }),
            new ExpectedTable("UriSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("Uri", "varchar", 256, "Latin1_General_100_CS_AS", false),
            }),
            new ExpectedTable("TokenText", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("Text", "nvarchar", 400, "Latin1_General_CI_AI", false),
                Col("IsHistory", "bit", null, null, false),
            }),
            new ExpectedTable("ReferenceTokenCompositeSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("BaseUri1", "varchar", 128, "Latin1_General_100_CS_AS", true),
                Col("ReferenceResourceTypeId1", "smallint", null, null, true),
                Col("ReferenceResourceId1", "varchar", 64, "Latin1_General_100_CS_AS", false),
                Col("ReferenceResourceVersion1", "int", null, null, true),
                Col("SystemId2", "int", null, null, true),
                Col("Code2", "varchar", 256, "Latin1_General_100_CS_AS", false),
                Col("CodeOverflow2", "varchar", null, "Latin1_General_100_CS_AS", true),
            }),
            new ExpectedTable("TokenDateTimeCompositeSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("SystemId1", "int", null, null, true),
                Col("Code1", "varchar", 256, "Latin1_General_100_CS_AS", false),
                Col("StartDateTime2", "datetime2", 7, null, false),
                Col("EndDateTime2", "datetime2", 7, null, false),
                Col("IsLongerThanADay2", "bit", null, null, false),
                Col("CodeOverflow1", "varchar", null, "Latin1_General_100_CS_AS", true),
            }),
            new ExpectedTable("TokenNumberNumberCompositeSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("SystemId1", "int", null, null, true),
                Col("Code1", "varchar", 256, "Latin1_General_100_CS_AS", false),
                Col("SingleValue2", "decimal", 36, null, true),
                Col("LowValue2", "decimal", 36, null, true),
                Col("HighValue2", "decimal", 36, null, true),
                Col("SingleValue3", "decimal", 36, null, true),
                Col("LowValue3", "decimal", 36, null, true),
                Col("HighValue3", "decimal", 36, null, true),
                Col("HasRange", "bit", null, null, false),
                Col("CodeOverflow1", "varchar", null, "Latin1_General_100_CS_AS", true),
            }),
            new ExpectedTable("TokenQuantityCompositeSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("SystemId1", "int", null, null, true),
                Col("Code1", "varchar", 256, "Latin1_General_100_CS_AS", false),
                Col("SystemId2", "int", null, null, true),
                Col("QuantityCodeId2", "int", null, null, true),
                Col("SingleValue2", "decimal", 36, null, true),
                Col("LowValue2", "decimal", 36, null, true),
                Col("HighValue2", "decimal", 36, null, true),
                Col("CodeOverflow1", "varchar", null, "Latin1_General_100_CS_AS", true),
            }),
            new ExpectedTable("TokenStringCompositeSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("SystemId1", "int", null, null, true),
                Col("Code1", "varchar", 256, "Latin1_General_100_CS_AS", false),
                Col("Text2", "nvarchar", 256, "Latin1_General_CI_AI", false),
                Col("TextOverflow2", "nvarchar", null, "Latin1_General_CI_AI", true),
                Col("CodeOverflow1", "varchar", null, "Latin1_General_100_CS_AS", true),
            }),
            new ExpectedTable("TokenTokenCompositeSearchParam", "dbo", new[]
            {
                Col("ResourceTypeId", "smallint", null, null, false),
                Col("ResourceSurrogateId", "bigint", null, null, false),
                Col("SearchParamId", "smallint", null, null, false),
                Col("SystemId1", "int", null, null, true),
                Col("Code1", "varchar", 256, "Latin1_General_100_CS_AS", false),
                Col("SystemId2", "int", null, null, true),
                Col("Code2", "varchar", 256, "Latin1_General_100_CS_AS", false),
                Col("CodeOverflow1", "varchar", null, "Latin1_General_100_CS_AS", true),
                Col("CodeOverflow2", "varchar", null, "Latin1_General_100_CS_AS", true),
            }),
        };

        [Fact]
        public void CatalogDefault_IsNotNull()
        {
            Assert.NotNull(_catalog);
        }

        [Theory]
        [MemberData(nameof(GetTableNames))]
        public void CatalogContainsTable(string tableName, string expectedSchema)
        {
            TableDescriptor table;
            try
            {
                table = _catalog.Table(tableName);
            }
            catch (KeyNotFoundException)
            {
                Assert.Fail($"SqlCatalog.Default is missing table '{expectedSchema}.{tableName}' required by schema 116 search-index contract.");
                return;
            }

            Assert.NotNull(table);
            Assert.Equal(
                expectedSchema,
                table.SchemaName,
                StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [MemberData(nameof(GetColumnFacts))]
        public void CatalogColumnMatchesSchema116(
            string tableName,
            string columnName,
            string expectedSqlType,
            int? expectedMaxLength,
            string expectedCollation,
            bool expectedNullable)
        {
            TableDescriptor table;
            try
            {
                table = _catalog.Table(tableName);
            }
            catch (KeyNotFoundException)
            {
                Assert.Fail($"Cannot verify column '{tableName}.{columnName}': table '{tableName}' is missing from SqlCatalog.Default.");
                return;
            }

            ColumnDescriptor column = table.Columns?.FirstOrDefault(c =>
                string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));

            Assert.True(
                column != null,
                $"Table '{tableName}' is missing column '{columnName}' (expected: {expectedSqlType}, nullable={expectedNullable}).");

            // Assert SQL type
            Assert.True(
                string.Equals(column.SqlType, expectedSqlType, StringComparison.OrdinalIgnoreCase),
                $"Column '{tableName}.{columnName}' type mismatch: expected='{expectedSqlType}', actual='{column.SqlType}'.");

            // Assert max length (only when expected is specified)
            if (expectedMaxLength.HasValue)
            {
                Assert.True(
                    column.MaxLength == expectedMaxLength,
                    $"Column '{tableName}.{columnName}' MaxLength mismatch: expected={expectedMaxLength}, actual={column.MaxLength}.");
            }

            // Assert collation (only when expected is specified)
            if (expectedCollation != null)
            {
                Assert.True(
                    string.Equals(column.Collation, expectedCollation, StringComparison.OrdinalIgnoreCase),
                    $"Column '{tableName}.{columnName}' Collation mismatch: expected='{expectedCollation}', actual='{column.Collation ?? "null"}'.");
            }

            // Assert nullability
            Assert.True(
                column.IsNullable == expectedNullable,
                $"Column '{tableName}.{columnName}' IsNullable mismatch: expected={expectedNullable}, actual={column.IsNullable}.");
        }

        public static IEnumerable<object[]> GetTableNames()
        {
            foreach (ExpectedTable t in Schema116Manifest)
            {
                yield return new object[] { t.TableName, t.SchemaName };
            }
        }

        public static IEnumerable<object[]> GetColumnFacts()
        {
            foreach (ExpectedTable t in Schema116Manifest)
            {
                foreach (ExpectedColumn c in t.Columns)
                {
                    yield return new object[] { t.TableName, c.Name, c.SqlType, c.MaxLength, c.Collation, c.IsNullable };
                }
            }
        }

        private static ExpectedColumn Col(string name, string sqlType, int? maxLength, string collation, bool isNullable)
            => new(name, sqlType, maxLength, collation, isNullable);

        private sealed record ExpectedTable(string TableName, string SchemaName, ExpectedColumn[] Columns);

        private sealed record ExpectedColumn(string Name, string SqlType, int? MaxLength, string Collation, bool IsNullable);
    }
}
