// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ignixa.Search.Expressions;
using Ignixa.Search.Sql.Ast;
using Ignixa.Search.Sql.Symbols;
using Ignixa.Specification.ValueSets.Normative;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Search;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.SqlServer.Features.Schema;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;
using IgnixaSearchOptions = Ignixa.Search.Models.SearchOptions;
using IgnixaSearchParameterInfo = Ignixa.Search.Models.SearchParameterInfo;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search.Ignixa
{
    /// <summary>
    /// Unit tests for <see cref="IgnixaSqlCompilerAdapter"/>, the compile-only adapter that invokes the
    /// coordinated Ignixa 0.6.32 / 0.6.32-alpha SQL compiler stages (Resolve, Lower, SqlBuilder) and
    /// returns an in-memory compilation artifact. These tests never execute SQL, open a connection, or
    /// hydrate resources.
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class IgnixaSqlCompilerAdapterTests
    {
        private static readonly Regex Sha256HexPattern = new("^[0-9A-Fa-f]{64}$", RegexOptions.Compiled);

        [Fact]
        public async Task CompileAsync_WhenResourceOnlySearchIsRequested_ReturnsParameterizedPlanArtifact()
        {
            // Arrange
            var model = Substitute.For<ISqlServerFhirModel>();
            model.TryGetResourceTypeId("Patient", out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)1;
                    return true;
                });

            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null);

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert
            Assert.True(result.Compiled);
            Assert.Null(result.FailureStage);
            Assert.Null(result.FailureKind);
            Assert.Null(result.FailureMessage);
            Assert.NotNull(result.LoweredPlan);
            Assert.NotNull(result.EmittedSql);
            Assert.Contains("dbo.Resource", result.EmittedSql!.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.UnresolvedParameters);
            Assert.NotEmpty(result.PlanFingerprint);
            Assert.Matches(Sha256HexPattern, result.PlanFingerprint);
            Assert.Equal("0.6.32", result.SearchPackageVersion);
            Assert.Equal("0.6.32-alpha", result.SearchSqlPackageVersion);
            Assert.Equal("0566dcb3e436a05afcdbcd581df702c79280693f", result.IgnixaCommit);
            Assert.Equal(SchemaVersionConstants.Max, result.SchemaVersion);
        }

        [Fact]
        public async Task CompileAsync_WhenSearchParameterIsUnresolved_ReturnsResolveCapabilityOutcome()
        {
            // Arrange
            var model = Substitute.For<ISqlServerFhirModel>();
            model.TryGetResourceTypeId("Patient", out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)1;
                    return true;
                });
            model.TryGetSearchParamId(Arg.Any<Uri>(), out Arg.Any<short>())
                .Returns(false);

            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));

            var parameter = new IgnixaSearchParameterInfo(
                "name",
                "name",
                SearchParamType.String,
                new Uri("http://hl7.org/fhir/SearchParameter/Patient-name"),
                components: null,
                expression: null,
                targetResourceTypes: null,
                baseResourceTypes: new[] { "Patient" },
                description: null);
            Expression expression = new SearchParameterExpression(
                parameter,
                new StringExpression(StringOperator.Equals, FieldName.String, componentIndex: null, "Smith", ignoreCase: false));

            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = expression);

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert
            Assert.False(result.Compiled);
            Assert.Equal("resolve", result.FailureStage);
            Assert.Equal("unresolved-symbol", result.FailureKind);
            Assert.Null(result.FailureMessage);
            Assert.Null(result.LoweredPlan);
            Assert.Null(result.EmittedSql);
            Assert.NotEmpty(result.UnresolvedParameters);
            Assert.Equal(string.Empty, result.PlanFingerprint);
        }

        [Fact]
        public async Task CompileAsync_WhenResolverThrows_PropagatesResolverException()
        {
            // Arrange
            var expectedException = new InvalidOperationException("Model not initialized");
            var resolver = Substitute.For<ISymbolResolver>();
            resolver.GetResourceTypeIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<short?>(expectedException));

            var adapter = CreateAdapter(resolver);
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null);

            // Act & Assert
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => adapter.CompileAsync(options, CancellationToken.None));
            Assert.Same(expectedException, thrown);
        }

        [Fact]
        public async Task CompileAsync_WhenCancelled_PropagatesOperationCanceledException()
        {
            // Arrange
            var model = Substitute.For<ISqlServerFhirModel>();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => adapter.CompileAsync(options, cts.Token));
        }

        [Fact]
        public async Task CompileAsync_WhenCountOnly_EmitsCountOnlyPlanWithoutIncludes()
        {
            // Arrange
            var model = Substitute.For<ISqlServerFhirModel>();
            model.TryGetResourceTypeId("Patient", out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)1;
                    return true;
                });

            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null, countOnly: true);

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert
            Assert.True(result.Compiled);
            Assert.NotNull(result.LoweredPlan);
            Assert.True(result.LoweredPlan!.Plan.CountOnly);
            Assert.Empty(result.LoweredPlan.Plan.Includes ?? (IReadOnlyList<IncludeStage>)Array.Empty<IncludeStage>());
        }

        private static IgnixaSqlCompilerAdapter CreateAdapter(ISymbolResolver resolver)
        {
            var schema = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max)
            {
                Current = SchemaVersionConstants.Max,
            };

            return new IgnixaSqlCompilerAdapter(
                resolver,
                schema,
                NullLogger<IgnixaSqlCompilerAdapter>.Instance);
        }

        private static SqlSearchOptions CreateOptions(Action<IgnixaSearchOptions> configureIgnixaOptions, bool countOnly = false)
        {
            var baseOptions = new SearchOptions
            {
                MaxItemCount = 10,
                CountOnly = countOnly,
            };

            var ignixaOptions = new IgnixaSearchOptions
            {
                ResourceType = "Patient",
                MaxItemCount = 10,
            };
            configureIgnixaOptions(ignixaOptions);

            return new SqlSearchOptions(baseOptions)
            {
                IgnixaOptions = ignixaOptions,
            };
        }
    }
}
