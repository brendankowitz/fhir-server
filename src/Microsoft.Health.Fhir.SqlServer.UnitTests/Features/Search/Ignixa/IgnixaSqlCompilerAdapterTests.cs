// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
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
using IgnixaSummaryType = Ignixa.Search.Models.SummaryType;

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
        // This assembly is FHIR-version-agnostic, so there is no compilation symbol to map. R4 is used as the\r
        // representative version: these tests exercise compartment expansion and symbol resolution, whose shapes\r
        // do not vary by version, and production supplies the real version through DI.\r
        private const global::Ignixa.Abstractions.FhirVersion IgnixaTestFhirVersion = global::Ignixa.Abstractions.FhirVersion.R4;

        // The real Ignixa definition managers rather than substitutes: both are self-contained (compiled from
        // the HL7 definitions for the active FHIR version) and are what the compiler consults to expand a
        // compartment search into reference search parameters, so stubbing them would only test the stub.
        private static readonly global::Ignixa.Search.Definition.ICompartmentDefinitionManager IgnixaCompartmentDefinitions =
            new global::Ignixa.Search.Definition.CompartmentDefinitionManager(IgnixaTestFhirVersion);

        private static readonly global::Ignixa.Search.Definition.ISearchParameterDefinitionManager IgnixaSearchParameterDefinitions =
            new global::Ignixa.Search.Definition.SearchableSearchParameterDefinitionManager(
                new global::Ignixa.Search.Definition.SearchParameterDefinitionManager(
                    global::Ignixa.Specification.Extensions.FhirSpecificationSchemaProviderExtensions.GetSchemaProvider(IgnixaTestFhirVersion),
                    NullLogger<global::Ignixa.Search.Definition.SearchParameterDefinitionManager>.Instance));

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

            // The version metadata is discovered from the build rather than hardcoded, so assert the
            // invariant that cannot drift: the stamps resolved to something real, and the recorded commit
            // is the one baked into the Ignixa assembly that actually emitted this SQL. Asserting literals
            // here is what let the previous constants claim 0.6.32 long after the branch moved to 0.6.101.
            Assert.NotEqual("unknown", result.SearchPackageVersion);
            Assert.NotEqual("unknown", result.SearchSqlPackageVersion);

            string ignixaInformationalVersion = typeof(global::Ignixa.Search.Sql.Builders.SqlBuilder).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion;
            string expectedCommit = ignixaInformationalVersion[(ignixaInformationalVersion.IndexOf('+', StringComparison.Ordinal) + 1)..];
            Assert.Equal(expectedCommit, result.IgnixaCommit);

            Assert.Equal(SchemaVersionConstants.Max, result.SchemaVersion);
        }

        [Fact]
        public async Task CompileAsync_WhenRowReturningSearchIsRequested_EmitsProjectionColumnsForTheExecutionReader()
        {
            // Arrange
            var model = CreateResolvableModel();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null);

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert: every dbo.Resource column the Ignixa execution reader materialises must appear in the
            // emitted SQL, bracket-quoted by the emitter, so the reader's ordinals line up with the projection.
            Assert.True(result.Compiled);
            Assert.NotNull(result.EmittedSql);
            foreach (string column in IgnixaResourceReader.ProjectionColumns)
            {
                Assert.Contains("[" + column + "]", result.EmittedSql!.Sql, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The legacy <c>AppendHistoryClause</c> / <c>AppendDeletedClause</c> truth table, expressed as
        /// (versionTypes, expected IsHistory filter, expected IsDeleted filter). A <see langword="null"/>
        /// expectation means no filter at all should be emitted on that axis.
        /// </summary>
        public static IEnumerable<object[]> GetVisibilityTruthTable()
        {
            return new[]
            {
                new object[] { ResourceVersionType.Latest, "IsHistory = 0", "IsDeleted = 0" },
                new object[] { ResourceVersionType.History, "IsHistory = 1", null },
                new object[] { ResourceVersionType.SoftDeleted, null, "IsDeleted = 1" },
                new object[] { ResourceVersionType.Latest | ResourceVersionType.History, null, "IsDeleted = 0" },
                new object[] { ResourceVersionType.Latest | ResourceVersionType.SoftDeleted, "IsHistory = 0", null },
                new object[] { ResourceVersionType.History | ResourceVersionType.SoftDeleted, "IsHistory = 1", "IsDeleted = 1" },
                new object[] { ResourceVersionType.Latest | ResourceVersionType.History | ResourceVersionType.SoftDeleted, null, null },
            };
        }

        [Theory]
        [MemberData(nameof(GetVisibilityTruthTable))]
        public async Task CompileAsync_ForEachResourceVersionType_EmitsTheLegacyVisibilityFilters(
            ResourceVersionType versionTypes,
            string expectedHistoryFilter,
            string expectedDeletedFilter)
        {
            // Each axis is filtered independently, so this covers not just the relaxations but the two exact
            // filters legacy emits for history-only and soft-deleted-only - the cases that used to be routed away
            // from Ignixa entirely. Asserting the *absence* of both polarities on an unconstrained axis is what
            // stops a mapping that always emits something from passing.
            var model = CreateResolvableModel();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null, countOnly: true);
            options.ResourceVersionTypes = versionTypes;

            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            Assert.True(result.Compiled);
            Assert.NotNull(result.EmittedSql);

            AssertAxis("IsHistory", expectedHistoryFilter, result.EmittedSql!.Sql);
            AssertAxis("IsDeleted", expectedDeletedFilter, result.EmittedSql.Sql);

            static void AssertAxis(string column, string expected, string sql)
            {
                if (expected == null)
                {
                    Assert.DoesNotContain($"{column} = 0", sql, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain($"{column} = 1", sql, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    Assert.Contains(expected, sql, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        [Fact]
        public async Task CompileAsync_WhenSurrogateKeysetContinuationTokenSet_EmitsForwardSeekAndBindsBoundary()
        {
            // Arrange: a default-order continuation token (no custom _sort) decodes to a composite
            // (ResourceTypeId=1 Patient, ResourceSurrogateId=5000) boundary with no sort value.
            var model = CreateResolvableModel();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null);
            options.ContinuationToken = "[1,5000]";

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert: the plan emits the forward composite keyset seek (T1, Sid1) > (@type, @sid) -- the exact
            // shape the legacy path applies as a GreaterThan on the partitioned primary key. Without this the
            // Ignixa plan would ignore the token and re-return page one.
            Assert.True(result.Compiled);
            Assert.NotNull(result.EmittedSql);
            Assert.Contains("Sid1 >", result.EmittedSql!.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("T1 >", result.EmittedSql.Sql, StringComparison.OrdinalIgnoreCase);

            // The boundary renders as bound parameters (never inlined), carrying the decoded type and surrogate id.
            Assert.Contains(result.EmittedSql.Parameters, p => p.Value is short s && s == 1);
            Assert.Contains(result.EmittedSql.Parameters, p => p.Value is long l && l == 5000L);
        }

        [Fact]
        public async Task CompileAsync_WhenCustomSortContinuationTokenSet_ReturnsPageCapabilityFailure()
        {
            // Arrange: a token that carries a sort value was minted for a custom sort. The surrogate-only keyset
            // cannot honour it, so the adapter declines rather than silently paging on the wrong boundary.
            var model = CreateResolvableModel();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null);
            options.ContinuationToken = "[\"2021-01-01T00:00:00.0000000\",1,5000]";

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert
            Assert.False(result.Compiled);
            Assert.Equal("page", result.FailureStage);
            Assert.Equal("continuation-token-sort-value", result.FailureKind);
        }

        [Fact]
        public async Task CompileAsync_WhenCountOnlySearchIsRequested_DoesNotProjectResourceColumns()
        {
            // Arrange
            var model = CreateResolvableModel();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));
            SqlSearchOptions options = CreateOptions(ignixaOptions => ignixaOptions.Expression = null, countOnly: true);

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert: a count-only plan emits a single scalar and must not carry the row projection, otherwise
            // the reader's GetInt64(0) count path would see resource columns instead of the count.
            Assert.True(result.Compiled);
            Assert.NotNull(result.EmittedSql);
            Assert.Contains("COUNT_BIG", result.EmittedSql!.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[RawResource]", result.EmittedSql.Sql, StringComparison.Ordinal);
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
        public async Task CompileAsync_WhenNoTargetResourceTypeIsNamed_CompilesAsASystemWideSearch()
        {
            // Arrange: no resource type and no _type filter is a system-wide search (GET /). The Ignixa
            // compiler lowers that to a MultiTypeResourceSource over every type, so this is no longer the
            // capability gap it was before multi-type support landed — it compiles. An unstubbed resolver
            // returns default (null) ids without throwing, which is fine here because a system-wide base
            // set needs no symbol lookups.
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
            Assert.True(result.Compiled);
            Assert.NotNull(result.LoweredPlan);

            // The base set must span every type rather than being scoped to one. An empty id list is what
            // "every type" means to the compiler, so assert the CTE kind rather than an id count.
            Assert.Contains(
                result.LoweredPlan!.Plan.Ctes,
                cte => cte is CteDefinition.MultiTypeResourceSource);
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

        // A token carrying a sort value was minted by the valued segment, so the next page continues there -
        // in BOTH directions. Direction alone would send the descending case to the missing segment.
        [InlineData(IgnixaSortOrder.Ascending, "[\"2021-01-01T00:00:00.0000000\",1,5000]", SortPhase.Valued)]
        [InlineData(IgnixaSortOrder.Descending, "[\"2021-01-01T00:00:00.0000000\",1,5000]", SortPhase.Valued)]

        // A token carrying no sort value was minted by the missing segment, so the next page continues there -
        // again in both directions.
        [InlineData(IgnixaSortOrder.Ascending, "[1,5000]", SortPhase.MissingPrimary)]
        [InlineData(IgnixaSortOrder.Descending, "[1,5000]", SortPhase.MissingPrimary)]
        public async Task CompileAsync_WhenCustomSortContinuationTokenSet_DerivesPhaseFromTheTokenNotTheDirection(
            IgnixaSortOrder sortOrder,
            string continuationToken,
            SortPhase expectedSortPhase)
        {
            // SortRewriter's branch order is not a single xor of direction and the second-phase flag once
            // continuation tokens exist: the token itself decides, because it was minted by the segment that
            // produced it. Getting this wrong does not mis-order rows subtly - Ignixa's EmitSeekPredicate
            // enforces boundary arity per phase, so a wrong phase either throws or re-serves the first page.
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
            options.ContinuationToken = continuationToken;

            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            Assert.True(result.Compiled, $"Expected compilation to succeed; stage={result.FailureStage}, kind={result.FailureKind}");
            Assert.Equal(expectedSortPhase, result.LoweredPlan!.Plan.Sort!.Phase);

            if (expectedSortPhase == SortPhase.Valued)
            {
                // The valued phase makes the one sort key active, so the seek needs exactly one boundary value,
                // typed like the datetime2 column rather than left as the token's round-trip string - binding the
                // string would order lexically.
                Assert.Contains(
                    result.EmittedSql!.Parameters,
                    p => p.Value is DateTime dt && dt == new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
            }
            else
            {
                // The missing phase drops the primary key, so the seek degenerates to the (T1, Sid1) tiebreak
                // and must carry no sort boundary at all.
                Assert.DoesNotContain(result.EmittedSql!.Parameters, p => p.Value is DateTime);
            }

            // Either way the composite identity boundary from the token is bound.
            Assert.Contains(result.EmittedSql!.Parameters, p => p.Value is short s && s == 1);
            Assert.Contains(result.EmittedSql.Parameters, p => p.Value is long l && l == 5000L);
        }

        [Theory]
        [InlineData(IgnixaSortOrder.Ascending, SortPhase.Valued)]
        [InlineData(IgnixaSortOrder.Descending, SortPhase.MissingPrimary)]
        public async Task CompileAsync_WhenSecondPhaseSentinelTokenSet_SwitchesSegmentAndDiscardsTheBoundary(
            IgnixaSortOrder sortOrder,
            SortPhase expectedSortPhase)
        {
            // SearchImpl mints a sentinel token (sentinel sort value, surrogate id 0) when the first segment
            // filled the page exactly and a probe found rows in the other segment. SortQuerySecondPhase is a
            // per-request field and is false on the fresh request carrying that token, so the sentinel must be
            // honoured on its own or page two repeats page one. Legacy discards the boundary entirely.
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
            options.ContinuationToken = $"[\"{SqlSearchConstants.SortSentinelValueForCt}\",0]";

            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            Assert.True(result.Compiled, $"Expected compilation to succeed; stage={result.FailureStage}, kind={result.FailureKind}");
            Assert.Equal(expectedSortPhase, result.LoweredPlan!.Plan.Sort!.Phase);

            // No boundary survives the sentinel: the other segment restarts from the top, exactly as legacy does.
            Assert.DoesNotContain(result.EmittedSql!.Parameters, p => p.Value is long l && l == 0L);
        }

        [Fact]
        public async Task CompileAsync_WhenCustomSortTokenOmitsTheResourceTypeSlot_SubstitutesTheSearchsOwnType()
        {
            // This is the shape a real custom-sort token actually has. SearchImpl mints one array slot per
            // SearchOptions.Sort entry, mapping _type to the type id and _lastUpdated to the surrogate id; a
            // "_sort=date" request has no _type entry, so the token is [sortValue, surrogateId] and carries no
            // type slot at all - legacy's sorted keyset compares Sid1 alone and never needed one. Ignixa's
            // PageSpec always carries a type boundary, so the adapter substitutes the search's own resource
            // type, which is exactly equivalent while the search is scoped to a single type. Before this the
            // second page of every custom sort fell back to legacy.
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
                ignixaOptions.Sort = new[] { new SortExpression(sortParameter, IgnixaSortOrder.Descending) };
            });
            options.ContinuationToken = "[\"2021-01-01T00:00:00.0000000\",5000]";

            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            Assert.True(result.Compiled, $"Expected compilation to succeed; stage={result.FailureStage}, kind={result.FailureKind}");
            Assert.Equal(SortPhase.Valued, result.LoweredPlan!.Plan.Sort!.Phase);

            // The substituted boundary is Patient's own type id (1 in the resolvable model), the sort value is
            // typed as a DateTime, and the surrogate id comes from the token.
            Assert.Contains(result.EmittedSql!.Parameters, p => p.Value is short s && s == 1);
            Assert.Contains(result.EmittedSql.Parameters, p => p.Value is long l && l == 5000L);
            Assert.Contains(
                result.EmittedSql.Parameters,
                p => p.Value is DateTime dt && dt == new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
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

        // ---------------------------------------------------------------------------
        // Task 6: count-only Observation with SummaryType.Count + resolvable include
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task CompileAsync_WhenObservationCountOnlyWithResolvableInclude_SuppressesIncludesInLoweredPlan()
        {
            // Arrange: the model resolves Patient (id=1), Observation (id=2), and the Observation-subject
            // search parameter (id=3) so that the include could theoretically be passed to Resolve.RunAsync.
            // Because CountOnly=true the adapter must suppress the include BEFORE calling Resolve, so
            // compilation succeeds and the lowered plan contains no include stages.
            var model = CreateObservationResolvableModel();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));

            SqlSearchOptions options = CreateOptions(
                ignixaOptions =>
                {
                    ignixaOptions.ResourceType = "Observation";
                    ignixaOptions.ResourceTypes = new[] { "Observation" };
                    ignixaOptions.Expression = null;
                    ignixaOptions.Summary = IgnixaSummaryType.Count;
                    ignixaOptions.Include = new[] { CreateIncludeExpression() };
                },
                countOnly: true);

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert
            Assert.True(result.Compiled, $"Expected compilation to succeed; stage={result.FailureStage}, kind={result.FailureKind}");
            Assert.NotNull(result.LoweredPlan);
            Assert.True(result.LoweredPlan!.Plan.CountOnly, "Lowered plan must be count-only.");
            Assert.Empty(result.LoweredPlan.Plan.Includes ?? (IReadOnlyList<IncludeStage>)Array.Empty<IncludeStage>());
        }

        // ---------------------------------------------------------------------------
        // Task 6: include shape — successful include stage in non-count-only plan
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task CompileAsync_WhenObservationIncludeIsRequested_LoweredPlanContainsIncludeStage()
        {
            // Arrange: non-count-only Observation search with an Observation.subject include; the model
            // resolves Patient (id=1), Observation (id=2), and the Observation-subject search parameter
            // (id=3). The lowered plan must contain exactly one forward include stage that carries the
            // correct reference search parameter ID, seed/output resource type IDs, SeedFromMatch,
            // Iterate, and Limit from the installed Ignixa 0.6.32-alpha API.
            var model = CreateObservationResolvableModel();
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));

            SqlSearchOptions options = CreateOptions(ignixaOptions =>
            {
                ignixaOptions.ResourceType = "Observation";
                ignixaOptions.ResourceTypes = new[] { "Observation" };
                ignixaOptions.Expression = null;
                ignixaOptions.Include = new[] { CreateIncludeExpression() };
                ignixaOptions.IncludesMaxItemCount = 100;
            });

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert — compilation and plan structure
            Assert.True(result.Compiled, $"Expected compilation to succeed; stage={result.FailureStage}, kind={result.FailureKind}");
            Assert.NotNull(result.LoweredPlan);
            IReadOnlyList<IncludeStage> includes = result.LoweredPlan!.Plan.Includes!;
            Assert.NotNull(includes);
            Assert.NotEmpty(includes);

            // The single include stage must carry the semantics of the Observation.subject include:
            // forward direction, reference search param id=3, Observation (2) seeds Patient (1) output.
            IncludeStage stage = includes[0];
            Assert.Equal(IncludeDirection.Forward, stage.Direction);
            Assert.Equal((short)3, stage.ReferenceSearchParamId);
            Assert.Contains((short)2, stage.SeedTypeIds);   // Observation is the seed resource type
            Assert.Contains((short)1, stage.OutputTypeIds); // Patient is the target/output resource type
            Assert.True(stage.SeedFromMatch, "Primary include must be seeded from the match result set (SeedFromMatch=true).");
            Assert.False(stage.Iterate, "Primary non-iterative include must have Iterate=false.");
            Assert.Equal(100, stage.Limit);
        }

        // ---------------------------------------------------------------------------
        // Task 6: telemetry redaction — adapter's narrow NotSupportedException log
        // must contain only safe metadata; the adapter must not emit on success.
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task CompileAsync_WhenLowerRejectionOccurs_StructuredLogContainsOnlyExceptionTypeAndNoUserData()
        {
            // Arrange: trigger the adapter's single logging path — the narrow NotSupportedException
            // catch around Lower.Run — by providing a null ResourceType which causes Lower.Run to
            // reject the request as a capability gap.
            //
            // The capturing logger proves the adapter IS logging (non-vacuous) and that only the
            // exception type class-name is emitted; the exception message, raw SQL, parameter names,
            // and any other user data MUST NOT appear in any structured-state value.
            var resolver = Substitute.For<ISymbolResolver>();
            var capturingLogger = new AdapterCapturingLogger();
            var schema = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max)
            {
                Current = SchemaVersionConstants.Max,
            };
            var adapter = new IgnixaSqlCompilerAdapter(resolver, schema, IgnixaCompartmentDefinitions, IgnixaSearchParameterDefinitions, capturingLogger);

            SqlSearchOptions options = CreateOptions(ignixaOptions =>
            {
                ignixaOptions.Expression = null;

                // The trigger must reach Lower, so it cannot depend on symbol resolution: an unstubbed
                // resolver returns null ids, which fails at Resolve and never gets there. _lastUpdated is a
                // resource-column sort key that needs no lookup, and Lower caps _sort at three keys, so four
                // of them is a pure count check that throws inside Lower with no I/O at all. The subject
                // under test is the redaction of the log entry, not the particular gap that provokes it.
                var lastUpdated = new IgnixaSearchParameterInfo(
                    "_lastUpdated",
                    "_lastUpdated",
                    SearchParamType.Date,
                    new Uri("http://hl7.org/fhir/SearchParameter/Resource-lastUpdated"));

                ignixaOptions.Sort = new[]
                {
                    new SortExpression(lastUpdated, IgnixaSortOrder.Ascending),
                    new SortExpression(lastUpdated, IgnixaSortOrder.Ascending),
                    new SortExpression(lastUpdated, IgnixaSortOrder.Ascending),
                    new SortExpression(lastUpdated, IgnixaSortOrder.Ascending),
                };
            });

            // Act
            IgnixaSqlCompilationOutcome result = await adapter.CompileAsync(options, CancellationToken.None);

            // Assert — outcome is a capability failure (lower / not-supported)
            Assert.False(result.Compiled);
            Assert.Equal("lower", result.FailureStage);
            Assert.Equal("not-supported", result.FailureKind);

            // The adapter must have emitted exactly one structured log entry from the catch block.
            Assert.Single(capturingLogger.Entries);
            AdapterLogEntry entry = capturingLogger.Entries[0];
            Assert.Equal(LogLevel.Information, entry.Level);

            // Only the two expected structured keys must be present:
            //   {ExceptionType}      — the exception class name (never a user value)
            //   {OriginalFormat}     — the message template
            Assert.True(entry.State.ContainsKey("ExceptionType"), "Structured log must contain 'ExceptionType' key.");
            Assert.True(entry.State.ContainsKey("{OriginalFormat}"), "Structured log must carry '{OriginalFormat}'.");

            // ExceptionType must carry only the safe class name, not the exception message.
            string exceptionTypeValue = entry.State["ExceptionType"]?.ToString() ?? string.Empty;
            Assert.Equal("NotSupportedException", exceptionTypeValue);

            // No structured state value (except the template) may carry raw SQL labels or parameter names.
            foreach (KeyValuePair<string, object> kvp in entry.State)
            {
                if (string.Equals(kvp.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    continue;
                }

                string valueStr = kvp.Value?.ToString() ?? string.Empty;
                Assert.DoesNotContain("EmittedSql", valueStr, StringComparison.Ordinal);
                Assert.DoesNotContain("@p0", valueStr, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task CompileAsync_WhenTwoSearchesHaveSameShapeButDifferentValues_ProduceSamePlanFingerprint()
        {
            // Proves that plan fingerprints are derived from the plan SHAPE only
            // (QueryPlan.Explain()) and not from literal search values.  Two searches whose
            // filter expressions use identical operators and field targets but different string
            // literals must produce identical fingerprints because the emitted SQL differs only
            // in its parameter value — which is never hashed.
            //
            // This is the key telemetry-safety property: the fingerprint can be logged and compared
            // without leaking any user-provided search value.
            var codeParamUri = new Uri("http://hl7.org/fhir/SearchParameter/Patient-code");
            var model = CreateResolvableModel(codeParamUri);
            var adapter = CreateAdapter(new IgnixaSqlSymbolResolver(model));

            var codeParameter = new IgnixaSearchParameterInfo(
                "code",
                "code",
                SearchParamType.Token,
                codeParamUri,
                components: null,
                expression: null,
                targetResourceTypes: null,
                baseResourceTypes: new[] { "Patient" },
                description: null);

            SqlSearchOptions optionsA = CreateOptions(ignixaOptions =>
            {
                ignixaOptions.Expression = Expression.SearchParameter(
                    codeParameter,
                    Expression.StartsWith(FieldName.TokenText, componentIndex: null, value: "alpha-sensitive-value", ignoreCase: true));
            });

            SqlSearchOptions optionsB = CreateOptions(ignixaOptions =>
            {
                ignixaOptions.Expression = Expression.SearchParameter(
                    codeParameter,
                    Expression.StartsWith(FieldName.TokenText, componentIndex: null, value: "BETA-COMPLETELY-DIFFERENT-VALUE", ignoreCase: true));
            });

            // Act
            IgnixaSqlCompilationOutcome resultA = await adapter.CompileAsync(optionsA, CancellationToken.None);
            IgnixaSqlCompilationOutcome resultB = await adapter.CompileAsync(optionsB, CancellationToken.None);

            // Assert — both must compile successfully
            Assert.True(resultA.Compiled, $"Expected options-A to compile; stage={resultA.FailureStage}, kind={resultA.FailureKind}");
            Assert.True(resultB.Compiled, $"Expected options-B to compile; stage={resultB.FailureStage}, kind={resultB.FailureKind}");

            // Both fingerprints must be valid 64-character uppercase SHA-256 hex strings.
            Assert.Matches(new Regex("^[0-9A-F]{64}$"), resultA.PlanFingerprint);
            Assert.Matches(new Regex("^[0-9A-F]{64}$"), resultB.PlanFingerprint);

            // The fingerprints must be identical: same operator + field target → same plan shape.
            Assert.Equal(resultA.PlanFingerprint, resultB.PlanFingerprint);
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
                IgnixaCompartmentDefinitions,
                IgnixaSearchParameterDefinitions,
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

        // ---------------------------------------------------------------------------
        // Task 6 helpers: Observation model, include expression, capturing logger
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Creates a model that resolves Patient (id=1), Observation (id=2), and the
        /// Observation-subject search parameter (id=3). Used for include and count-only tests.
        /// </summary>
        private static ISqlServerFhirModel CreateObservationResolvableModel()
        {
            var model = Substitute.For<ISqlServerFhirModel>();
            model.TryGetResourceTypeId("Patient", out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)1;
                    return true;
                });
            model.TryGetResourceTypeId("Observation", out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)2;
                    return true;
                });
            model.TryGetSearchParamId(
                new Uri("http://hl7.org/fhir/SearchParameter/Observation-subject"),
                out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)3;
                    return true;
                });
            return model;
        }

        /// <summary>
        /// Creates a valid forward <see cref="IncludeExpression"/> for Observation.subject targeting
        /// Patient, using the 0.6.32 <see cref="IgnixaSearchParameterInfo"/> constructor. Does not
        /// construct include values from raw query strings.
        /// </summary>
        private static IncludeExpression CreateIncludeExpression()
        {
            var reference = new IgnixaSearchParameterInfo(
                "subject",
                "subject",
                SearchParamType.Reference,
                new Uri("http://hl7.org/fhir/SearchParameter/Observation-subject"),
                components: null,
                expression: null,
                targetResourceTypes: new[] { "Patient" },
                baseResourceTypes: new[] { "Observation" },
                description: null);

            return new IncludeExpression(
                new[] { "Observation" },
                reference,
                "Observation",
                "Patient",
                new[] { "Patient" },
                wildCard: false,
                reversed: false,
                iterate: false);
        }

        /// <summary>
        /// A single captured log event including the log level, formatted message, and the raw
        /// structured-state key-value pairs emitted by the adapter's logging calls.
        /// </summary>
        private sealed class AdapterLogEntry
        {
            public AdapterLogEntry(LogLevel level, string message, IReadOnlyDictionary<string, object> state)
            {
                Level = level;
                Message = message;
                State = state;
            }

            public LogLevel Level { get; }

            public string Message { get; }

            /// <summary>
            /// Raw structured-state key-value pairs, including <c>{OriginalFormat}</c>.
            /// </summary>
            public IReadOnlyDictionary<string, object> State { get; }
        }

        /// <summary>
        /// An <see cref="ILogger{IgnixaSqlCompilerAdapter}"/> that captures every log entry — both the
        /// formatted message string and the raw structured-state key-value pairs — so tests can assert
        /// that no sensitive search values or raw SQL content appears in adapter telemetry.
        /// </summary>
        private sealed class AdapterCapturingLogger : ILogger<IgnixaSqlCompilerAdapter>
        {
            private readonly List<AdapterLogEntry> _entries = new();

            public IReadOnlyList<AdapterLogEntry> Entries => _entries;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                var stateFields = new Dictionary<string, object>(StringComparer.Ordinal);
                if (state is IEnumerable<KeyValuePair<string, object>> structured)
                {
                    foreach (KeyValuePair<string, object> kvp in structured)
                    {
                        stateFields[kvp.Key] = kvp.Value;
                    }
                }

                _entries.Add(new AdapterLogEntry(logLevel, formatter(state, exception) ?? string.Empty, stateFields));
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();

                private NullScope()
                {
                }

                public void Dispose()
                {
                }
            }
        }
    }
}
