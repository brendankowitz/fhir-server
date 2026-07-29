// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ignixa.Search.Sql.Ast;
using Ignixa.Search.Sql.Symbols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Registration;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Search;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Registration;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.SqlServer.Features.Schema;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using IgnixaSearchOptions = Ignixa.Search.Models.SearchOptions;
using IgnixaSearchParameterInfo = Ignixa.Search.Models.SearchParameterInfo;
using IgnixaSortOrder = Ignixa.Search.Expressions.SortOrder;
using SortExpression = Ignixa.Search.Expressions.SortExpression;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search.Ignixa
{
    /// <summary>
    /// Unit tests for <see cref="IgnixaSqlCompileOnlyRouter"/>.
    /// Verifies skip conditions, compilation invocation, outcome handling, and exception propagation.
    /// No SQL is executed; no connection is opened; no response is replaced.
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class IgnixaSqlCompileOnlyRouterTests
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

        // ---------------------------------------------------------------------------
        // Disabled-by-default
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ObserveAsync_WhenDisabledByDefault_DoesNotCompile()
        {
            // Arrange: default config (EnableIgnixaSqlCompileOnly = false)
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var config = new FhirSqlServerConfiguration(); // EnableIgnixaSqlCompileOnly defaults to false
            var router = CreateRouter(adapter, config);
            SqlSearchOptions options = CreateEligibleOptions();

            // Act
            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert: compile was never invoked
            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        // ---------------------------------------------------------------------------
        // Version-type skip conditions — covers all non-Latest-only flag combinations
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Every non-empty <see cref="ResourceVersionType"/> combination. The compiler's tri-state
        /// <see cref="Ignixa.Search.Sql.Ast.ResourceVisibility"/> filters the IsHistory and IsDeleted axes
        /// independently, so all seven reproduce the legacy truth table and none is gated.
        /// <list type="bullet">
        ///   <item>1  = Latest</item>
        ///   <item>2  = History</item>
        ///   <item>3  = Latest | History</item>
        ///   <item>4  = SoftDeleted</item>
        ///   <item>5  = Latest | SoftDeleted</item>
        ///   <item>6  = History | SoftDeleted</item>
        ///   <item>7  = Latest | History | SoftDeleted</item>
        /// </list>
        /// </summary>
        public static IEnumerable<object[]> GetVersionTypes()
        {
            return Enumerable.Range(1, 7).Select(x => new object[] { x });
        }

        [Theory]
        [MemberData(nameof(GetVersionTypes))]
        public async Task ObserveAsync_ForAnyResourceVersionType_IsEligibleAndCompiles(int rawVersionType)
        {
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.ResourceVersionTypes = (ResourceVersionType)rawVersionType;

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        // ---------------------------------------------------------------------------
        // Per-field skip conditions
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ObserveAsync_WhenAccessControlPredicateRequiredAndNotTranslated_DoesNotCompile()
        {
            // The default. SearchOptionsFactory only sets IgnixaAccessControlTranslated once it has proved the
            // request's access control fully expressible as an Ignixa allow-list, so anything it did not translate
            // -- and any control it has never heard of -- reaches this gate with the flag false and stays on the
            // legacy path, which still enforces it.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();

            Assert.False(options.IgnixaAccessControlTranslated);

            await router.ObserveAsync(options, accessControlPredicateRequired: true, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenAccessControlPredicateRequiredAndTranslated_Compiles()
        {
            // The scopes reached IgnixaOptions.AllowedResourceTypes, where the compiler enforces them structurally
            // on the match set and on every include stage, so routing to Ignixa applies the same restriction the
            // legacy generator would. Without this case the allow-list work would be unreachable.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaOptions.AllowedResourceTypes = new[] { "Patient", "Observation" };
            options.IgnixaAccessControlTranslated = true;

            await router.ObserveAsync(options, accessControlPredicateRequired: true, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenTranslatedButNoAccessControlPredicateRequired_Compiles()
        {
            // The flag only ever opens the gate; it is never itself a reason to compile. A request with no access
            // control at all must behave exactly as before this gate existed.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task CloneSqlSearchOptions_PreservesAllowedResourceTypes()
        {
            // The router clones options on every row-returning search to bump MaxItemCount for page detection, and
            // the clone -- not the original -- is what gets compiled. A clone that dropped the allow-list would
            // compile a plan enforcing nothing while every gate above still reported the request as authorized:
            // a fail-open bypass that leaves no trace.
            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaOptions.AllowedResourceTypes = new[] { "Patient" };
            options.IgnixaAccessControlTranslated = true;

            SqlSearchOptions clone = options.CloneSqlSearchOptions();

            Assert.True(clone.IgnixaAccessControlTranslated);
            Assert.Equal(new[] { "Patient" }, clone.IgnixaOptions.AllowedResourceTypes);

            await Task.CompletedTask;
        }

        [Fact]
        public async Task ObserveAsync_WhenFeedRangeSet_StillCompiles()
        {
            // FeedRange is a Cosmos physical-partition token consumed only by FhirCosmosSearchService. The SQL
            // search service never reads it and GetFeedRanges is unimplemented for SQL, so it cannot change the
            // rows a SQL query returns and must not gate routing.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.FeedRange = "some-feed-range";

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenContinuationTokenIsUnparseable_DoesNotCompile()
        {
            // An opaque/unparseable token has no (ResourceTypeId, ResourceSurrogateId) boundary to translate
            // into a PageSpec, so it stays on the legacy path.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.ContinuationToken = "some-continuation-token";

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenSurrogateKeysetContinuationTokenSet_IsEligibleAndCompiles()
        {
            // A default-order continuation token (no custom _sort) carries a composite
            // (ResourceTypeId, ResourceSurrogateId) boundary and no sort value. The compiler reproduces the
            // legacy forward keyset seek via PageSpec, so the request is eligible for the Ignixa path.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.ContinuationToken = "[3,5000]";

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenCustomSortContinuationTokenSet_IsEligibleAndCompiles()
        {
            // A token that carries a sort value was minted by the valued segment of a single-key custom _sort.
            // The adapter reconstructs that keyed boundary as a one-value PageSpec, so the request is eligible
            // rather than being pushed back onto the legacy path.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaOptions.Sort = new List<SortExpression>
            {
                new SortExpression(CreateDateSortParameter("date"), IgnixaSortOrder.Ascending),
            };
            options.ContinuationToken = "[\"2021-01-01T00:00:00.0000000\",3,5000]";

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenMultiKeySortContinuationTokenSet_DoesNotCompile()
        {
            // The continuation token carries exactly one SortValue, so a two-key sort has no boundary value
            // for its second key and its lexicographic seek cannot be reconstructed. That must stay on legacy
            // rather than seek from a boundary the compiler would reject for having the wrong arity.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaOptions.Sort = new List<SortExpression>
            {
                new SortExpression(CreateDateSortParameter("date"), IgnixaSortOrder.Ascending),
                new SortExpression(CreateDateSortParameter("issued"), IgnixaSortOrder.Ascending),
            };
            options.ContinuationToken = "[\"2021-01-01T00:00:00.0000000\",3,5000]";

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenTypelessContinuationTokenSetOnASingleTypeSearch_IsEligibleAndCompiles()
        {
            // A custom-sort token is [sortValue, surrogateId] and never carries a ResourceTypeId slot, because
            // legacy's sorted keyset compares Sid1 alone. Within a single-type search the search's own type id
            // is the boundary the token omitted, so the adapter can reconstruct the composite seek and the
            // request stays on the Ignixa path.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.ContinuationToken = "5000";

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenTypelessContinuationTokenSetOnAnUnsortedMultiTypeSearch_DoesNotCompile()
        {
            // With more than one target type there is no single constant ResourceTypeId to substitute for the
            // boundary the token omitted. Ignixa can seek without a type boundary, but only for a custom sort
            // key, whose ORDER BY carries no type term; an unsorted search orders type-major, so a
            // surrogate-only seek would disagree with it and drop rows across the page seam.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaOptions.ResourceTypes = new List<string> { "Patient", "Observation" };
            options.ContinuationToken = "5000";

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenTypelessContinuationTokenSetOnAMultiTypeCustomSort_Compiles()
        {
            // The shape that closed the last continuation-token gate. A custom-sort token is
            // [sortValue, surrogateId] and never carries a ResourceTypeId slot; across several types there is
            // none to substitute either. Ignixa emits this sort's ORDER BY as (sortValue, Sid1) with no type
            // term - a total order, because ResourceSurrogateId is globally unique - so a seek that omits the
            // type boundary agrees with it and the page is sound.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaOptions.ResourceTypes = new List<string> { "Patient", "Observation" };
            options.IgnixaOptions.Sort = new List<SortExpression>
            {
                new SortExpression(CreateDateSortParameter("date"), IgnixaSortOrder.Ascending),
            };

            // Two slots, no type: exactly what SqlServerSearchService mints for a custom sort.
            options.ContinuationToken = "[\"2021-01-01T00:00:00.0000000\",5000]";

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenIncludesContinuationTokenIsUnparseable_DoesNotCompile()
        {
            // The includes gate is now narrowed: a plain (unsorted) $includes page routes to Ignixa's IncludesOnly
            // capability, but an includes token that does not parse is still legacy's -- SearchIncludeImpl raises a
            // BadRequest on it, and Ignixa must not serve an unbounded include stream in its place. "some-includes-
            // token" is not a valid token, so the gate closes and the adapter is never asked to compile.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IncludesContinuationToken = "some-includes-token";

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenIncludesContinuationTokenIsFirstPage_Compiles()
        {
            // A plain first-page $includes token ([MatchTypeId, min, max], no include cursor, no second phase) is
            // exactly the shape the IncludesOnly path serves, so the gate must let it through to the adapter. The
            // mock returns a capability failure only to keep the assertion on "was CompileAsync reached", not on
            // the emitted SQL -- the end-to-end SQL is covered by the integration differentials.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IncludesContinuationToken = new IncludesContinuationToken(new object[] { (short)1, 1L, 100L }).ToString();

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenIncludesContinuationTokenIsSecondPhaseSort_IsEligibleAndCompiles()
        {
            // SortQuerySecondPhase only records which half of the sorted missing/valued partition produced the
            // matches in this token's surrogate range. SqlServerSearchService copies it onto the options before
            // running the page and the adapter maps it onto Ignixa's SortPhase, so the compiler can express it.
            // The differential test GivenASecondPhaseSortedIncludesToken_... proves the emitted page agrees with
            // legacy, including excluding the phase-1 rows that sit inside the same range.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IncludesContinuationToken = new IncludesContinuationToken(new object[] { (short)1, 1L, 100L, null, null, true }).ToString();

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenIncludesContinuationTokenNestsASecondPhaseToken_IsEligibleAndCompiles()
        {
            // A nested SecondPhaseContinuationToken is orchestration rather than compilation: SqlServerSearchService
            // runs this page, re-feeds the nested token as an ordinary six-slot token, runs a second page and
            // concatenates the two. Nothing nested reaches the emitter - TryBuildIncludesOnlyWindow reads only the
            // surrogate range and the include cursor - so each page compiles as an ordinary includes page. The
            // differential test GivenANestedSecondPhaseIncludesToken_... proves the stitched result agrees with
            // legacy, delivering every included resource of both sort phases exactly once.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            var innerToken = new IncludesContinuationToken(new object[] { (short)1, 50L, 100L, null, null, true });

            SqlSearchOptions options = CreateEligibleOptions();
            options.IncludesContinuationToken = new IncludesContinuationToken(
                new object[] { (short)1, 1L, 49L, null, null, false, innerToken.ToJson() }).ToString();

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenIgnoreSearchParamHashSet_IsEligibleAndCompiles()
        {
            // IgnoreSearchParamHash is read only by SearchForReindexInternalAsync, a separate search entry point
            // that never invokes this router. On the main search path that does invoke the router the flag is
            // inert, so legacy and Ignixa produce identical rows regardless of it. The gate is therefore removed
            // and such a request is eligible for the Ignixa path.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnoreSearchParamHash = true;

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenQueryHintsArePresent_DoesNotCompileRegardlessOfTheAsyncFlag()
        {
            // Hint-carrying requests are intercepted by SearchImpl before the router, so this gate is a backstop
            // against that interception changing. It keys on the hints alone: a hint carrier that somehow arrived
            // without the async flag would steer legacy plan construction just the same.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IsAsyncOperation = false;
            options.QueryHints = new List<(string Param, string Value)> { ("EndSurrogateId", "1") };

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenIsAsyncOperationSetWithQueryHints_DoesNotCompile()
        {
            // Query hints steer legacy plan construction (surrogate-id windowing, custom command timeouts) in ways
            // the differential suite does not cover, so a hinted async page stays on legacy even though the async
            // flag on its own no longer blocks routing.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IsAsyncOperation = true;
            options.QueryHints = new List<(string Param, string Value)> { ("EndSurrogateId", "1") };

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenIsAsyncOperationSetWithoutQueryHints_IsEligibleAndCompiles()
        {
            // IsAsyncOperation by itself is legacy plan shaping, not a compiler capability. MaxItemCount is
            // resolved onto the shared SearchOptions before either engine sees it, and the only other effect is the
            // surrogate-id contribution to the legacy query-plan-reuse hash, which belongs to a parameter manager
            // Ignixa does not use.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IsAsyncOperation = true;

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenUnsupportedSearchParamsPresent_DoesNotCompile()
        {
            // The two engines disagreed about what to drop (the agreement flag is false), so routing to Ignixa
            // would return a different row set than legacy in one direction or the other.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.UnsupportedSearchParams = new List<Tuple<string, string>> { Tuple.Create("_unknown", "value") };
            options.IgnixaUnsupportedParamsAgreeWithLegacy = false;

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenBothEnginesDroppedTheSameUnsupportedParams_IsEligibleAndCompiles()
        {
            // An unsupported parameter is only a routing hazard when the engines disagree about it. When both
            // ignored the same parameter the row sets are identical and the bundle reports the same issue either
            // way, so the request belongs on the Ignixa path like any other.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.UnsupportedSearchParams = new List<Tuple<string, string>> { Tuple.Create("_unknown", "value") };
            options.IgnixaUnsupportedParamsAgreeWithLegacy = true;

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenIgnixaSortDisagreesWithLegacy_DoesNotCompile()
        {
            // The storage layer's sorting validator discards the whole sort when SQL cannot honour it, while
            // Ignixa binds and applies its own. Routing such a request would return the same rows in a different
            // order - and on a paged search, a different window of rows entirely.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaSortAgreesWithLegacy = false;

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenSmartCompartmentSearch_DoesNotCompile()
        {
            // SECURITY BOUNDARY. A SMART compartment definition expands membership through
            // SmartCompartmentSearchRewriter, which does not agree with the standard compartment expansion Ignixa
            // would apply. Unlike the claims-driven SMART shapes this one arrives with no AccessControlContext, so
            // the access-control gate never sees it and this gate is the only thing keeping it on legacy.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaSmartCompartmentSearch = true;

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenStandardCompartmentSearch_IsEligibleAndCompiles()
        {
            // The negative half of the gate: a plain compartment search carries no SMART definition, so
            // AppendIgnixaCompartmentExpression's CompartmentSearchExpression is the correct membership and the
            // request routes like any other.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaSmartCompartmentSearch = false;

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenIgnixaOptionsNull_DoesNotCompile()
        {
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            // Create options without IgnixaOptions
            var baseOptions = new SearchOptions { MaxItemCount = 10 };
            var options = new SqlSearchOptions(baseOptions); // IgnixaOptions is null

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ObserveAsync_WhenIgnixaOptionsResourceTypeNull_IsEligibleAndCompiles()
        {
            // A null resource type is a system-level search (GET /), which the compiler supports via
            // LowerOptions.SystemLevelSearch. The gate that used to reject it is now open.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaOptions.ResourceType = null;

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        [Fact]
        public async Task ObserveAsync_WhenIgnixaOptionsResourceTypesHasMultipleTypes_IsEligibleAndCompiles()
        {
            // A multi-_type search is supported by the compiler via LowerOptions.ResourceTypes; the gate that
            // used to reject more than one type is now open.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));
            var router = CreateRouter(adapter, EnabledConfig());

            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaOptions.ResourceTypes = new List<string> { "Patient", "Observation" };

            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        // ---------------------------------------------------------------------------
        // Eligible request — adapter invoked exactly once
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ObserveAsync_WhenEligibleRequest_InvokesAdapterExactlyOnce()
        {
            // Arrange
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));

            var router = CreateRouter(adapter, EnabledConfig());
            SqlSearchOptions options = CreateEligibleOptions();

            // Act
            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert: adapter called exactly once; no execution dependencies
            await adapter.Received(1).CompileAsync(options, CancellationToken.None);
        }

        // ---------------------------------------------------------------------------
        // Compiled outcome — LoweredPlan invariant
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ObserveAsync_WhenCompiledTrueButLoweredPlanIsNull_ThrowsInvalidOperationException()
        {
            // Arrange: adapter reports success but violates the LoweredPlan invariant
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var brokenOutcome = new IgnixaSqlCompilationOutcome(
                Compiled: true,
                FailureStage: null,
                FailureKind: null,
                FailureMessage: null,
                LoweredPlan: null,          // invariant violation
                EmittedSql: null,
                UnresolvedParameters: Array.Empty<IgnixaSearchParameterInfo>(),
                SearchPackageVersion: "0.6.32",
                SearchSqlPackageVersion: "0.6.32-alpha",
                IgnixaCommit: "abc123",
                SchemaVersion: 72,
                PlanFingerprint: "AABBCC");
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(brokenOutcome);

            var router = CreateRouter(adapter, EnabledConfig());
            SqlSearchOptions options = CreateEligibleOptions();

            // Act + Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None));
        }

        [Fact]
        public async Task ObserveAsync_WhenCompiledSuccessfully_LogsMetadataAndDoesNotThrow()
        {
            // Arrange: use the real adapter + resolvable model so we get a genuine LoweredPlan
            var model = Substitute.For<ISqlServerFhirModel>();
            model.TryGetResourceTypeId("Patient", out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)1;
                    return true;
                });

            var schema = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max)
            {
                Current = SchemaVersionConstants.Max,
            };
            var realAdapter = new IgnixaSqlCompilerAdapter(
                new IgnixaSqlSymbolResolver(model),
                schema,
                IgnixaCompartmentDefinitions,
                IgnixaSearchParameterDefinitions,
                NullLogger<IgnixaSqlCompilerAdapter>.Instance);

            var router = new IgnixaSqlCompileOnlyRouter(
                realAdapter,
                EnabledConfig(),
                NullLogger<IgnixaSqlCompileOnlyRouter>.Instance);

            SqlSearchOptions options = CreateEligibleOptions();

            // Act + Assert: no exception; metadata is logged internally
            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);
        }

        // ---------------------------------------------------------------------------
        // Capability failure outcome
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ObserveAsync_WhenCapabilityFailure_LogsMetadataAndDoesNotThrow()
        {
            // Arrange
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("resolve", "unresolved-symbol"));

            var router = CreateRouter(adapter, EnabledConfig());
            SqlSearchOptions options = CreateEligibleOptions();

            // Act + Assert: capability failure must not throw; only metadata is logged
            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);
        }

        // ---------------------------------------------------------------------------
        // Exception / cancellation propagation
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ObserveAsync_WhenAdapterThrowsUnexpectedException_PropagatesException()
        {
            // Arrange
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("adapter-failure"));

            var router = CreateRouter(adapter, EnabledConfig());
            SqlSearchOptions options = CreateEligibleOptions();

            // Act + Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None));
        }

        [Fact]
        public async Task ObserveAsync_WhenCancellationRequested_PropagatesCancellation()
        {
            // Arrange
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new OperationCanceledException());

            var router = CreateRouter(adapter, EnabledConfig());
            SqlSearchOptions options = CreateEligibleOptions();

            // Act + Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None));
        }

        // ---------------------------------------------------------------------------
        // Argument validation
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ObserveAsync_WhenSearchOptionsNull_ThrowsArgumentNullException()
        {
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, EnabledConfig());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => router.ObserveAsync(null, accessControlPredicateRequired: false, CancellationToken.None));
        }

        // ---------------------------------------------------------------------------
        // Logging content assertions — capability failure (structured state)
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ObserveAsync_WhenCapabilityFailure_StructuredEventContainsAllowedKeysAndOmitsSensitiveData()
        {
            // Arrange: use two unresolved params so UnresolvedCount exercises a non-trivial value.
            const string stage = "resolve";
            const string kind = "unresolved-symbol";
            const string sentinel = "SentinelResourceMustNotAppearInStructuredState";

            var unresolvedParams = new[]
            {
                new IgnixaSearchParameterInfo("code1", "code1"),
                new IgnixaSearchParameterInfo("code2", "code2"),
            };
            var capabilityOutcome = new IgnixaSqlCompilationOutcome(
                Compiled: false,
                FailureStage: stage,
                FailureKind: kind,
                FailureMessage: null,
                LoweredPlan: null,
                EmittedSql: null,
                UnresolvedParameters: unresolvedParams,
                SearchPackageVersion: "0.6.32",
                SearchSqlPackageVersion: "0.6.32-alpha",
                IgnixaCommit: "abc123",
                SchemaVersion: 72,
                PlanFingerprint: string.Empty);

            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(capabilityOutcome);

            var logger = new CapturingLogger<IgnixaSqlCompileOnlyRouter>();
            var router = new IgnixaSqlCompileOnlyRouter(adapter, EnabledConfig(), logger);

            // Use a sentinel resource type — must never appear in any structured-state value.
            SqlSearchOptions options = CreateEligibleOptions();
            options.IgnixaOptions.ResourceType = sentinel;
            options.IgnixaOptions.ResourceTypes = new List<string> { sentinel };

            // Act
            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert: exactly one Information-level entry
            List<LogEntry> infoEntries = logger.Entries
                .Where(e => e.Level == LogLevel.Information)
                .ToList();
            Assert.Single(infoEntries);
            LogEntry entry = infoEntries[0];

            // --- Allowed metadata keys must be present ---
            Assert.True(entry.State.ContainsKey("Stage"), "Structured event must contain 'Stage' key.");
            Assert.Equal(stage, entry.State["Stage"]);

            Assert.True(entry.State.ContainsKey("Kind"), "Structured event must contain 'Kind' key.");
            Assert.Equal(kind, entry.State["Kind"]);

            Assert.True(entry.State.ContainsKey("UnresolvedCount"), "Structured event must contain 'UnresolvedCount' key.");
            Assert.Equal(2, entry.State["UnresolvedCount"]);

            Assert.True(entry.State.ContainsKey("Fingerprint"), "Structured event must contain 'Fingerprint' key.");

            Assert.True(entry.State.ContainsKey("{OriginalFormat}"), "Structured event must carry '{OriginalFormat}'.");

            // --- Disallowed keys must NOT be present ---
            Assert.False(
                entry.State.ContainsKey("FailureMessage"),
                "'FailureMessage' must not appear as a structured key — it is not an allowed metadata field.");

            // --- No structured value (except the template itself) may carry the sentinel ---
            foreach (KeyValuePair<string, object> kvp in entry.State)
            {
                if (!string.Equals(kvp.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain(
                        sentinel,
                        kvp.Value?.ToString() ?? string.Empty,
                        StringComparison.Ordinal);
                }
            }
        }

        // ---------------------------------------------------------------------------
        // Logging content assertions — successful compiled outcome (structured state)
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ObserveAsync_WhenCompiledSuccessfully_StructuredEventContainsAllowedKeysAndOmitsSensitiveData()
        {
            // Arrange: use the real adapter so Fingerprint, CteCount, etc. are genuine values
            // produced by the compiler. A Patient-only search has no includes, no sort, and is
            // not count-only — those fields are independently verifiable.
            var model = Substitute.For<ISqlServerFhirModel>();
            model.TryGetResourceTypeId("Patient", out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)1;
                    return true;
                });
            var schema = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max)
            {
                Current = SchemaVersionConstants.Max,
            };
            var realAdapter = new IgnixaSqlCompilerAdapter(
                new IgnixaSqlSymbolResolver(model),
                schema,
                IgnixaCompartmentDefinitions,
                IgnixaSearchParameterDefinitions,
                NullLogger<IgnixaSqlCompilerAdapter>.Instance);

            var logger = new CapturingLogger<IgnixaSqlCompileOnlyRouter>();
            var router = new IgnixaSqlCompileOnlyRouter(realAdapter, EnabledConfig(), logger);
            SqlSearchOptions options = CreateEligibleOptions();

            // Act
            await router.ObserveAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert: exactly one Information-level entry
            List<LogEntry> infoEntries = logger.Entries
                .Where(e => e.Level == LogLevel.Information)
                .ToList();
            Assert.Single(infoEntries);
            LogEntry entry = infoEntries[0];

            // --- Required structured keys ---
            Assert.True(entry.State.ContainsKey("Fingerprint"), "Structured event must contain 'Fingerprint'.");
            Assert.True(entry.State.ContainsKey("CteCount"), "Structured event must contain 'CteCount'.");
            Assert.True(entry.State.ContainsKey("IncludeCount"), "Structured event must contain 'IncludeCount'.");
            Assert.True(entry.State.ContainsKey("HasSort"), "Structured event must contain 'HasSort'.");
            Assert.True(entry.State.ContainsKey("CountOnly"), "Structured event must contain 'CountOnly'.");
            Assert.True(entry.State.ContainsKey("SchemaVersion"), "Structured event must contain 'SchemaVersion'.");
            Assert.True(entry.State.ContainsKey("{OriginalFormat}"), "Structured event must carry '{OriginalFormat}'.");

            // --- Values for known-constant fields ---
            // Fingerprint is a non-empty SHA-256 hex string for successful compilations.
            Assert.NotEmpty((string)entry.State["Fingerprint"]);

            // No includes/sort/count-only in a plain Patient search.
            Assert.Equal("0", entry.State["IncludeCount"]?.ToString());
            Assert.Equal("False", entry.State["HasSort"]?.ToString());
            Assert.Equal("False", entry.State["CountOnly"]?.ToString());
            Assert.Equal(
                SchemaVersionConstants.Max.ToString(CultureInfo.InvariantCulture),
                entry.State["SchemaVersion"]?.ToString());

            // --- No structured value (except the template) may carry the resource type ---
            foreach (KeyValuePair<string, object> kvp in entry.State)
            {
                if (!string.Equals(kvp.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain(
                        "Patient",
                        kvp.Value?.ToString() ?? string.Empty,
                        StringComparison.Ordinal);
                }
            }
        }

        // ---------------------------------------------------------------------------
        // Configuration default
        // ---------------------------------------------------------------------------

        [Fact]
        public void FhirSqlServerConfiguration_EnableIgnixaSqlCompileOnly_DefaultsToFalse()
        {
            // The feature is off-by-default; any positive-confirmation test requires opt-in.
            var config = new FhirSqlServerConfiguration();
            Assert.False(config.EnableIgnixaSqlCompileOnly);
        }

        // ---------------------------------------------------------------------------
        // Service registration — descriptor-only checks (no service resolution)
        // ---------------------------------------------------------------------------

        [Fact]
        public void AddSqlServer_RegistersIgnixaSqlCompilerAdapterAsScopedServiceContract()
        {
            // Arrange: descriptors are added at registration time; no dependency is resolved here.
            var services = new ServiceCollection();
            var builder = new TestFhirServerBuilder(services);

            // Act — pass a no-op configure action so AddSqlServerConnection does not throw.
            builder.AddSqlServer(_ => { });

            // Assert: service-contract descriptor (IIgnixaSqlCompilerAdapter → scoped)
            Assert.True(
                services.Any(d =>
                    d.ServiceType == typeof(IIgnixaSqlCompilerAdapter) &&
                    d.Lifetime == ServiceLifetime.Scoped),
                "IIgnixaSqlCompilerAdapter must be registered as a scoped service by AddSqlServer.");

            // Assert: AsSelf descriptor (IgnixaSqlCompilerAdapter → scoped)
            Assert.True(
                services.Any(d =>
                    d.ServiceType == typeof(IgnixaSqlCompilerAdapter) &&
                    d.Lifetime == ServiceLifetime.Scoped),
                "IgnixaSqlCompilerAdapter.AsSelf must be registered as scoped by AddSqlServer.");
        }

        [Fact]
        public void AddSqlServer_RegistersIgnixaSqlCompileOnlyRouterAsScopedServiceContract()
        {
            // Arrange
            var services = new ServiceCollection();
            var builder = new TestFhirServerBuilder(services);

            // Act — pass a no-op configure action so AddSqlServerConnection does not throw.
            builder.AddSqlServer(_ => { });

            // Assert: service-contract descriptor (IIgnixaSqlCompileOnlyRouter → scoped)
            Assert.True(
                services.Any(d =>
                    d.ServiceType == typeof(IIgnixaSqlCompileOnlyRouter) &&
                    d.Lifetime == ServiceLifetime.Scoped),
                "IIgnixaSqlCompileOnlyRouter must be registered as a scoped service by AddSqlServer.");

            // Assert: AsSelf descriptor (IgnixaSqlCompileOnlyRouter → scoped)
            Assert.True(
                services.Any(d =>
                    d.ServiceType == typeof(IgnixaSqlCompileOnlyRouter) &&
                    d.Lifetime == ServiceLifetime.Scoped),
                "IgnixaSqlCompileOnlyRouter.AsSelf must be registered as scoped by AddSqlServer.");
        }

        [Fact]
        public void AddSqlServer_KeepsIgnixaSqlSymbolResolverRegisteredAsScoped()
        {
            // Arrange
            var services = new ServiceCollection();
            var builder = new TestFhirServerBuilder(services);

            // Act
            builder.AddSqlServer(_ => { });

            // Assert: the pre-existing ISymbolResolver scoped registration is intact
            Assert.True(
                services.Any(d =>
                    d.ServiceType == typeof(ISymbolResolver) &&
                    d.Lifetime == ServiceLifetime.Scoped),
                "ISymbolResolver (IgnixaSqlSymbolResolver) must remain registered as scoped.");
        }

        // ---------------------------------------------------------------------------
        // TryCreateExecutionPlanAsync — execution routing (cutover step 1)
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task TryCreateExecutionPlanAsync_WhenExecutionDisabledByDefault_ReturnsNullAndDoesNotCompile()
        {
            // Arrange: EnableIgnixaSqlExecution defaults to false.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, new FhirSqlServerConfiguration());
            SqlSearchOptions options = CreateEligibleOptions();

            // Act
            IgnixaSqlExecutionPlan plan = await router.TryCreateExecutionPlanAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert
            Assert.Null(plan);
            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task TryCreateExecutionPlanAsync_WhenRequestIsIneligible_ReturnsNullAndDoesNotCompile()
        {
            // Arrange: a continuation token makes the request ineligible.
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            var router = CreateRouter(adapter, ExecutionEnabledConfig());
            SqlSearchOptions options = CreateEligibleOptions();
            options.ContinuationToken = "token";

            // Act
            IgnixaSqlExecutionPlan plan = await router.TryCreateExecutionPlanAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert
            Assert.Null(plan);
            await adapter.DidNotReceive().CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task TryCreateExecutionPlanAsync_WhenCompilationReportsCapabilityGap_ReturnsNull()
        {
            // Arrange
            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>())
                .Returns(CreateCapabilityFailureOutcome("lower", "not-supported"));
            var router = CreateRouter(adapter, ExecutionEnabledConfig());
            SqlSearchOptions options = CreateEligibleOptions();

            // Act
            IgnixaSqlExecutionPlan plan = await router.TryCreateExecutionPlanAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert
            Assert.Null(plan);
        }

        [Fact]
        public async Task TryCreateExecutionPlanAsync_WhenEligibleRowSearch_ReturnsExecutablePlanWithParameters()
        {
            // Arrange: a real compiler over a resolvable Patient model produces genuine emitted SQL.
            var router = CreateRouter(CreateRealAdapter(), ExecutionEnabledConfig());
            SqlSearchOptions options = CreateEligibleOptions();

            // Act
            IgnixaSqlExecutionPlan plan = await router.TryCreateExecutionPlanAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert
            Assert.NotNull(plan);
            Assert.False(plan.CountOnly);
            Assert.False(plan.HasIncludes);
            Assert.NotNull(plan.EmittedSql);
            Assert.False(string.IsNullOrWhiteSpace(plan.EmittedSql.Sql));

            // Every emitted parameter must carry an @-prefixed name and a bindable value, so the search service
            // can bind them onto the SqlCommand verbatim.
            Assert.All(plan.EmittedSql.Parameters, p =>
            {
                Assert.StartsWith("@", p.Name, StringComparison.Ordinal);
                Assert.NotNull(p.Value);
            });
        }

        [Fact]
        public async Task TryCreateExecutionPlanAsync_WhenCountOnlySearch_ReturnsCountOnlyPlan()
        {
            // Arrange
            var router = CreateRouter(CreateRealAdapter(), ExecutionEnabledConfig());
            SqlSearchOptions options = CreateEligibleCountOnlyOptions();

            // Act
            IgnixaSqlExecutionPlan plan = await router.TryCreateExecutionPlanAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert
            Assert.NotNull(plan);
            Assert.True(plan.CountOnly);
            Assert.False(plan.HasIncludes);
            Assert.Contains("COUNT_BIG", plan.EmittedSql.Sql, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------------
        // Includes materialisation guard (narrowed): plain _include/_revinclude execute on
        // Ignixa; wildcard and :iterate includes fall back to legacy.
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task TryCreateExecutionPlanAsync_WhenPlainIncludeSearch_ReturnsExecutablePlanCarryingIncludes()
        {
            // Arrange: a real compiler over an Observation-resolvable model produces a genuine plan whose
            // single include stage is a plain forward Observation.subject -> Patient include (non-iterate,
            // reference param id resolved). The narrowed guard must let this onto the Ignixa path.
            var router = CreateRouter(CreateObservationRealAdapter(), ExecutionEnabledConfig());
            SqlSearchOptions options = CreateEligibleIncludeOptions();

            // Act
            IgnixaSqlExecutionPlan plan = await router.TryCreateExecutionPlanAsync(options, accessControlPredicateRequired: false, CancellationToken.None);

            // Assert: the plan is returned and flagged as carrying includes, so the reader materialises the
            // (T1, Sid1, IsMatch, IsPartial, ...) row shape.
            Assert.NotNull(plan);
            Assert.True(plan.HasIncludes);
            Assert.False(plan.CountOnly);
            Assert.NotNull(plan.EmittedSql);
            Assert.False(string.IsNullOrWhiteSpace(plan.EmittedSql.Sql));
        }

        [Fact]
        public async Task TryCreateExecutionPlanAsync_WhenIterateInclude_ProducesAnIncludeCarryingPlan()
        {
            // Arrange: start from a genuine compiled plain include, then flip its single stage to Iterate=true.
            // Ignixa resolves :iterate as a single topological pass rather than legacy's fixed-point iteration.
            // The router used to decline for that reason; the closure the two produce is now proven equal by the
            // iterate-include differential in SqlServerIgnixaExecutionTests, so the stage is accepted and must
            // still be flagged as carrying includes for the reader's row shape.
            IgnixaSqlCompilationOutcome mutated = MutateSingleIncludeStage(
                await CompilePlainIncludeOutcomeAsync(),
                stage => stage with { Iterate = true });

            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>()).Returns(mutated);
            var router = CreateRouter(adapter, ExecutionEnabledConfig());

            // Act
            IgnixaSqlExecutionPlan plan = await router.TryCreateExecutionPlanAsync(CreateEligibleIncludeOptions(), accessControlPredicateRequired: false, CancellationToken.None);

            // Assert
            Assert.NotNull(plan);
            Assert.True(plan.HasIncludes);
        }

        [Fact]
        public async Task TryCreateExecutionPlanAsync_WhenWildcardInclude_ProducesAnIncludeCarryingPlan()
        {
            // Arrange: start from a genuine compiled plain include, then flip its single stage to a wildcard
            // (ReferenceSearchParamId == null), which lowers to a reference-parameter-less join. Previously
            // declined as unvalidated; the wildcard-include differential in SqlServerIgnixaExecutionTests now
            // compares that row set against legacy, so the router accepts it.
            IgnixaSqlCompilationOutcome mutated = MutateSingleIncludeStage(
                await CompilePlainIncludeOutcomeAsync(),
                stage => stage with { ReferenceSearchParamId = null });

            var adapter = Substitute.For<IIgnixaSqlCompilerAdapter>();
            adapter.CompileAsync(Arg.Any<SqlSearchOptions>(), Arg.Any<CancellationToken>()).Returns(mutated);
            var router = CreateRouter(adapter, ExecutionEnabledConfig());

            // Act
            IgnixaSqlExecutionPlan plan = await router.TryCreateExecutionPlanAsync(CreateEligibleIncludeOptions(), accessControlPredicateRequired: false, CancellationToken.None);

            // Assert
            Assert.NotNull(plan);
            Assert.True(plan.HasIncludes);
        }

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static IgnixaSqlCompileOnlyRouter CreateRouter(
            IIgnixaSqlCompilerAdapter adapter,
            FhirSqlServerConfiguration configuration)
        {
            return new IgnixaSqlCompileOnlyRouter(
                adapter,
                configuration,
                NullLogger<IgnixaSqlCompileOnlyRouter>.Instance);
        }

        private static FhirSqlServerConfiguration EnabledConfig() =>
            new FhirSqlServerConfiguration { EnableIgnixaSqlCompileOnly = true };

        private static FhirSqlServerConfiguration ExecutionEnabledConfig() =>
            new FhirSqlServerConfiguration { EnableIgnixaSqlExecution = true };

        /// <summary>
        /// Builds a real <see cref="IgnixaSqlCompilerAdapter"/> over a resolvable Patient model, so
        /// execution-plan tests exercise genuine emitted SQL rather than a hand-crafted outcome.
        /// </summary>
        private static IgnixaSqlCompilerAdapter CreateRealAdapter()
        {
            var model = Substitute.For<ISqlServerFhirModel>();
            model.TryGetResourceTypeId("Patient", out Arg.Any<short>())
                .Returns(callInfo =>
                {
                    callInfo[1] = (short)1;
                    return true;
                });

            var schema = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max)
            {
                Current = SchemaVersionConstants.Max,
            };

            return new IgnixaSqlCompilerAdapter(
                new IgnixaSqlSymbolResolver(model),
                schema,
                IgnixaCompartmentDefinitions,
                IgnixaSearchParameterDefinitions,
                NullLogger<IgnixaSqlCompilerAdapter>.Instance);
        }

        private static SqlSearchOptions CreateEligibleCountOnlyOptions()
        {
            SqlSearchOptions options = CreateEligibleOptions();
            options.CountOnly = true;
            return options;
        }

        /// <summary>
        /// Creates a fully eligible <see cref="SqlSearchOptions"/> that passes all skip conditions.
        /// </summary>
        private static SqlSearchOptions CreateEligibleOptions()
        {
            var baseOptions = new SearchOptions
            {
                MaxItemCount = 10,

                // SearchOptionsFactory is the only production writer of this flag, and it is what makes an
                // unsorted request eligible; a hand-built options object has to opt in the same way.
                IgnixaSortAgreesWithLegacy = true,
            };

            // ResourceVersionTypes defaults to Latest — eligible.
            return new SqlSearchOptions(baseOptions)
            {
                IgnixaOptions = new IgnixaSearchOptions
                {
                    ResourceType = "Patient",
                    ResourceTypes = new List<string> { "Patient" },
                    MaxItemCount = 10,
                    Expression = null,
                },
            };
        }

        private static IgnixaSqlCompilationOutcome CreateCapabilityFailureOutcome(
            string stage,
            string kind)
        {
            return new IgnixaSqlCompilationOutcome(
                Compiled: false,
                FailureStage: stage,
                FailureKind: kind,
                FailureMessage: null,
                LoweredPlan: null,
                EmittedSql: null,
                UnresolvedParameters: Array.Empty<IgnixaSearchParameterInfo>(),
                SearchPackageVersion: "0.6.32",
                SearchSqlPackageVersion: "0.6.32-alpha",
                IgnixaCommit: "abc123",
                SchemaVersion: 72,
                PlanFingerprint: string.Empty);
        }

        /// <summary>
        /// Builds a real <see cref="IgnixaSqlCompilerAdapter"/> over a model that resolves Patient (id=1),
        /// Observation (id=2), and the Observation-subject search parameter (id=3), so include tests exercise
        /// genuine emitted include SQL rather than a hand-crafted outcome.
        /// </summary>
        private static IgnixaSqlCompilerAdapter CreateObservationRealAdapter()
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

            var schema = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max)
            {
                Current = SchemaVersionConstants.Max,
            };

            return new IgnixaSqlCompilerAdapter(
                new IgnixaSqlSymbolResolver(model),
                schema,
                IgnixaCompartmentDefinitions,
                IgnixaSearchParameterDefinitions,
                NullLogger<IgnixaSqlCompilerAdapter>.Instance);
        }

        /// <summary>
        /// A date-typed sort parameter, the shape a custom <c>_sort</c> continuation token is minted for.
        /// </summary>
        private static IgnixaSearchParameterInfo CreateDateSortParameter(string code)
        {
            return new IgnixaSearchParameterInfo(
                code,
                code,
                global::Ignixa.Specification.ValueSets.Normative.SearchParamType.Date,
                new Uri($"http://hl7.org/fhir/SearchParameter/clinical-{code}"),
                components: null,
                expression: null,
                targetResourceTypes: null,
                baseResourceTypes: new[] { "Observation" },
                description: null);
        }

        /// <summary>
        /// A fully eligible row search on Observation carrying a single plain forward
        /// Observation.subject -> Patient include.
        /// </summary>
        private static SqlSearchOptions CreateEligibleIncludeOptions()
        {
            var reference = new IgnixaSearchParameterInfo(
                "subject",
                "subject",
                global::Ignixa.Specification.ValueSets.Normative.SearchParamType.Reference,
                new Uri("http://hl7.org/fhir/SearchParameter/Observation-subject"),
                components: null,
                expression: null,
                targetResourceTypes: new[] { "Patient" },
                baseResourceTypes: new[] { "Observation" },
                description: null);

            var include = new global::Ignixa.Search.Expressions.IncludeExpression(
                new[] { "Observation" },
                reference,
                "Observation",
                "Patient",
                new[] { "Patient" },
                wildCard: false,
                reversed: false,
                iterate: false);

            var baseOptions = new SearchOptions
            {
                MaxItemCount = 10,
                IgnixaSortAgreesWithLegacy = true,
            };

            return new SqlSearchOptions(baseOptions)
            {
                IgnixaOptions = new IgnixaSearchOptions
                {
                    ResourceType = "Observation",
                    ResourceTypes = new List<string> { "Observation" },
                    MaxItemCount = 10,
                    Expression = null,
                    Include = new[] { include },
                    IncludesMaxItemCount = 100,
                },
            };
        }

        /// <summary>
        /// Compiles the plain include options through a real adapter and returns the genuine, successful
        /// outcome (with real emitted SQL) so guard tests can mutate the single stage without inventing a plan.
        /// </summary>
        private static async Task<IgnixaSqlCompilationOutcome> CompilePlainIncludeOutcomeAsync()
        {
            IgnixaSqlCompilerAdapter adapter = CreateObservationRealAdapter();
            IgnixaSqlCompilationOutcome outcome = await adapter.CompileAsync(CreateEligibleIncludeOptions(), CancellationToken.None);
            Assert.True(outcome.Compiled, $"Expected plain include to compile; stage={outcome.FailureStage}, kind={outcome.FailureKind}");
            return outcome;
        }

        /// <summary>
        /// Returns a copy of <paramref name="outcome"/> whose single include stage has been replaced by
        /// <paramref name="mutate"/>. Reuses the genuine emitted SQL so the router sees a valid compiled outcome
        /// that differs only in the include stage shape under test.
        /// </summary>
        private static IgnixaSqlCompilationOutcome MutateSingleIncludeStage(
            IgnixaSqlCompilationOutcome outcome,
            Func<IncludeStage, IncludeStage> mutate)
        {
            QueryPlan plan = outcome.LoweredPlan.Plan;
            IReadOnlyList<IncludeStage> stages = plan.Includes;
            Assert.NotNull(stages);
            Assert.Single(stages);

            QueryPlan mutatedPlan = plan with { Includes = new[] { mutate(stages[0]) } };
            return outcome with { LoweredPlan = outcome.LoweredPlan with { Plan = mutatedPlan } };
        }

        // ---------------------------------------------------------------------------
        // Inner helpers: capturing logger and minimal IFhirServerBuilder stub
        // ---------------------------------------------------------------------------

        /// <summary>
        /// A single captured log event including the formatted message and the raw
        /// structured-state fields that the logger call emitted.
        /// </summary>
        private sealed class LogEntry
        {
            public LogEntry(LogLevel level, string message, IReadOnlyDictionary<string, object> state)
            {
                Level = level;
                Message = message;
                State = state;
            }

            public LogLevel Level { get; }

            public string Message { get; }

            /// <summary>
            /// Raw structured-state key-value pairs, including the special
            /// <c>{OriginalFormat}</c> key that carries the message template.
            /// </summary>
            public IReadOnlyDictionary<string, object> State { get; }
        }

        /// <summary>
        /// An <see cref="ILogger{T}"/> that records every log entry so tests can assert on
        /// both the formatted message string and the raw structured-state key-value pairs.
        /// </summary>
        private sealed class CapturingLogger<T> : ILogger<T>
        {
            private readonly List<LogEntry> _entries = new();

            public IReadOnlyList<LogEntry> Entries => _entries;

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

                _entries.Add(new LogEntry(logLevel, formatter(state, exception), stateFields));
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

        /// <summary>
        /// Minimal <see cref="IFhirServerBuilder"/> stub used by registration-descriptor tests.
        /// No services are resolved; only descriptors are examined.
        /// </summary>
        private sealed class TestFhirServerBuilder : IFhirServerBuilder
        {
            public TestFhirServerBuilder(IServiceCollection services) => Services = services;

            public IServiceCollection Services { get; }
        }
    }
}
