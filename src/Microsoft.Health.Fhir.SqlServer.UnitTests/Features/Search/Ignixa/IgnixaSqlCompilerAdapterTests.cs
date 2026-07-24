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
using Ignixa.Search.Sql.Lowering;
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
using IgnixaSortOrder = Ignixa.Search.Expressions.SortOrder;

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

        [Fact]
        public async Task CompileAsync_WhenLowerRejectsMissingTargetResourceType_ReturnsLowerCapabilityOutcome()
        {
            // Arrange: an unstubbed resolver returns default (null) ids without throwing, so Resolve.RunAsync
            // completes with no unresolved symbols; Lower.Run itself rejects the missing target resource type
            // as a capability gap via the narrow NotSupportedException boundary.
            var resolver = Substitute.For<ISymbolResolver>();
            var adapter = CreateAdapter(resolver);
            SqlSearchOptions options = CreateOptions(ignixaOptions =>
            {
                ignixaOptions.Expression = null;
                ignixaOptions.ResourceType = null;
                ignixaOptions.ResourceTypes = Array.Empty<string>();
            });

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert
            Assert.False(result.Compiled);
            Assert.Equal("lower", result.FailureStage);
            Assert.Equal("not-supported", result.FailureKind);
            Assert.NotNull(result.FailureMessage);
            Assert.Null(result.LoweredPlan);
            Assert.Null(result.EmittedSql);
            Assert.Equal(string.Empty, result.PlanFingerprint);
        }

        [Theory]
        [InlineData(IgnixaSortOrder.Ascending, false, SortPhase.MissingPrimary)]
        [InlineData(IgnixaSortOrder.Ascending, true, SortPhase.Valued)]
        [InlineData(IgnixaSortOrder.Descending, false, SortPhase.Valued)]
        [InlineData(IgnixaSortOrder.Descending, true, SortPhase.MissingPrimary)]
        public async Task CompileAsync_WhenSortingWithMissingValues_LowersExpectedSortPhase(
            IgnixaSortOrder sortOrder,
            bool sortQuerySecondPhase,
            SortPhase expectedSortPhase)
        {
            // Arrange
            var sortParamUri = new Uri("http://hl7.org/fhir/SearchParameter/Patient-birthdate");
            var sortParameter = new IgnixaSearchParameterInfo(
                "birthdate",
                "birthdate",
                SearchParamType.Date,
                sortParamUri,
                components: null,
                expression: null,
                targetResourceTypes: null,
                baseResourceTypes: new[] { "Patient" },
                description: null);
            var model = CreateResolvableModel(sortParamUri);
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));

            SqlSearchOptions options = CreateOptions(ignixaOptions =>
            {
                ignixaOptions.Expression = null;
                ignixaOptions.Sort = new[] { new SortExpression(sortParameter, sortOrder) };
            });
            options.SortQuerySecondPhase = sortQuerySecondPhase;

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert
            Assert.True(result.Compiled);
            Assert.NotNull(result.LoweredPlan);
            Assert.NotNull(result.LoweredPlan!.Plan.Sort);
            Assert.Equal(expectedSortPhase, result.LoweredPlan.Plan.Sort!.Phase);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CompileAsync_WhenSortingByLastUpdated_AlwaysUsesValuedSortPhase(bool sortQuerySecondPhase)
        {
            // Arrange
            var model = CreateResolvableModel();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            var lastUpdatedParameter = new IgnixaSearchParameterInfo(
                "_lastUpdated",
                "_lastUpdated",
                SearchParamType.Date,
                url: null,
                components: null,
                expression: null,
                targetResourceTypes: null,
                baseResourceTypes: new[] { "Patient" },
                description: null);

            SqlSearchOptions options = CreateOptions(ignixaOptions =>
            {
                ignixaOptions.Expression = null;
                ignixaOptions.Sort = new[] { new SortExpression(lastUpdatedParameter, IgnixaSortOrder.Ascending) };
            });
            options.SortQuerySecondPhase = sortQuerySecondPhase;

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert
            Assert.True(result.Compiled);
            Assert.NotNull(result.LoweredPlan);
            Assert.NotNull(result.LoweredPlan!.Plan.Sort);
            Assert.Equal(SortPhase.Valued, result.LoweredPlan.Plan.Sort!.Phase);
        }

        [Fact]
        public async Task CompileAsync_WhenCountOnlyWithUnresolvableInclude_SuppressesIncludeBeforeResolve()
        {
            // Arrange: the include references a search parameter the model cannot resolve. If a count-only
            // request incorrectly carried includes into Resolve.RunAsync, this would surface as an
            // unresolved-symbol capability failure. Because count-only requests must suppress includes before
            // Resolve is ever invoked, compilation succeeds instead.
            var model = CreateResolvableModel();
            model.TryGetSearchParamId(Arg.Any<Uri>(), out Arg.Any<short>()).Returns(false);

            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));

            var includeParameter = new IgnixaSearchParameterInfo(
                "organization",
                "organization",
                SearchParamType.Reference,
                new Uri("http://hl7.org/fhir/SearchParameter/Patient-organization"),
                components: null,
                expression: null,
                targetResourceTypes: new[] { "Organization" },
                baseResourceTypes: new[] { "Patient" },
                description: null);
            var include = new IncludeExpression(
                new[] { "Patient" },
                includeParameter,
                "Patient",
                targetResourceType: null,
                referencedTypes: new[] { "Organization" },
                wildCard: false,
                reversed: false,
                iterate: false);

            SqlSearchOptions options = CreateOptions(
                ignixaOptions =>
                {
                    ignixaOptions.Expression = null;
                    ignixaOptions.Include = new[] { include };
                },
                countOnly: true);

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert
            Assert.True(result.Compiled);
            Assert.Empty(result.UnresolvedParameters);
            Assert.True(result.LoweredPlan!.Plan.CountOnly);
            Assert.Empty(result.LoweredPlan.Plan.Includes ?? (IReadOnlyList<IncludeStage>)Array.Empty<IncludeStage>());
        }

        [Fact]
        public async Task ValidateResultShape_WhenCountOnlyMismatches_ReturnsCountOnlyMismatchCapability()
        {
            // Arrange: a real, successful compile provides a genuine baseline plan; the test then asks
            // for a shape different from the one that plan actually has, proving the classification branch
            // deterministically rather than relying on an undocumented divergence in real lowering behavior.
            (IgnixaSqlCompilerAdapter adapter, SqlSearchOptions options, LoweredPlan baseline) = await CreateBaselineAsync();
            var mismatchedOptions = new SqlSearchOptions(options) { IgnixaOptions = options.IgnixaOptions };
            mismatchedOptions.CountOnly = !baseline.Plan.CountOnly;

            // Act
            IgnixaSqlCompilationOutcome result = adapter.ValidateResultShape(mismatchedOptions, options.IgnixaOptions, baseline);

            // Assert
            Assert.NotNull(result);
            Assert.False(result!.Compiled);
            Assert.Equal("shape", result.FailureStage);
            Assert.Equal("count-only-mismatch", result.FailureKind);
            Assert.Equal(string.Empty, result.PlanFingerprint);
        }

        [Fact]
        public async Task ValidateResultShape_WhenIncludeCountMismatches_ReturnsIncludeShapeMismatchCapability()
        {
            // Arrange
            (IgnixaSqlCompilerAdapter adapter, SqlSearchOptions options, LoweredPlan baseline) = await CreateBaselineAsync();
            var extraInclude = new IncludeStage(
                Direction: IncludeDirection.Forward,
                ReferenceSearchParamId: null,
                SeedTypeIds: Array.Empty<short>(),
                OutputTypeIds: Array.Empty<short>(),
                SeedStages: Array.Empty<int>(),
                SeedFromMatch: true,
                Iterate: false,
                Limit: 0);
            QueryPlan mismatchedPlan = new(
                baseline.Plan.Ctes,
                baseline.Plan.Match,
                baseline.Plan.Top,
                baseline.Plan.OuterPredicate,
                new[] { extraInclude },
                baseline.Plan.Sort,
                baseline.Plan.Page,
                baseline.Plan.CountOnly);
            var mismatchedLowered = new LoweredPlan(mismatchedPlan, baseline.Provenance);

            // Act
            IgnixaSqlCompilationOutcome result = adapter.ValidateResultShape(options, options.IgnixaOptions, mismatchedLowered);

            // Assert
            Assert.NotNull(result);
            Assert.False(result!.Compiled);
            Assert.Equal("shape", result.FailureStage);
            Assert.Equal("include-shape-mismatch", result.FailureKind);
        }

        [Fact]
        public async Task ValidateResultShape_WhenSortRequestedButPlanHasNoSort_ReturnsSortShapeMismatchCapability()
        {
            // Arrange: the baseline plan was lowered without a sort. Requesting a sort against that same
            // plan proves the sort-shape-mismatch branch without needing Lower.Run to actually diverge.
            (IgnixaSqlCompilerAdapter adapter, SqlSearchOptions options, LoweredPlan baseline) = await CreateBaselineAsync();
            Assert.Null(baseline.Plan.Sort);

            var sortParameter = new IgnixaSearchParameterInfo("birthdate", "birthdate");
            options.IgnixaOptions.Sort = new[] { new SortExpression(sortParameter, IgnixaSortOrder.Ascending) };

            // Act
            IgnixaSqlCompilationOutcome result = adapter.ValidateResultShape(options, options.IgnixaOptions, baseline);

            // Assert
            Assert.NotNull(result);
            Assert.False(result!.Compiled);
            Assert.Equal("shape", result.FailureStage);
            Assert.Equal("sort-shape-mismatch", result.FailureKind);
        }

        [Fact]
        public async Task ValidateResultShape_WhenTopMismatches_ReturnsTopShapeMismatchCapability()
        {
            // Arrange
            (IgnixaSqlCompilerAdapter adapter, SqlSearchOptions options, LoweredPlan baseline) = await CreateBaselineAsync();
            var mismatchedOptions = new SqlSearchOptions(options) { IgnixaOptions = options.IgnixaOptions };
            mismatchedOptions.MaxItemCount = (baseline.Plan.Top ?? 0) + 1;

            // Act
            IgnixaSqlCompilationOutcome result = adapter.ValidateResultShape(mismatchedOptions, options.IgnixaOptions, baseline);

            // Assert
            Assert.NotNull(result);
            Assert.False(result!.Compiled);
            Assert.Equal("shape", result.FailureStage);
            Assert.Equal("top-shape-mismatch", result.FailureKind);
        }

        private static async Task<(IgnixaSqlCompilerAdapter Adapter, SqlSearchOptions Options, LoweredPlan Baseline)> CreateBaselineAsync()
        {
            var model = CreateResolvableModel();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null);

            IgnixaSqlCompilationOutcome baseline = await adapter.CompileAsync(options, CancellationToken.None);
            Assert.True(baseline.Compiled);

            return (adapter, options, baseline.LoweredPlan!);
        }

        private static ISqlServerFhirModel CreateResolvableModel(Uri sortParamUri = null)
        {
            var model = Substitute.For<ISqlServerFhirModel>();
            model.TryGetResourceTypeId("Patient", out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)1;
                    return true;
                });

            if (sortParamUri != null)
            {
                model.TryGetSearchParamId(sortParamUri, out Arg.Any<short>())
                    .Returns(callInfo =>
                    {
                        callInfo[1] = (short)2;
                        return true;
                    });
            }

            return model;
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
