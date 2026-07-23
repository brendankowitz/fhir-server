# Ignixa Search SQL Data Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the duplicated FHIR Server search parser and SQL rewriter path with the `Ignixa.Search` canonical expression model and `Ignixa.Search.Sql` compiler while preserving Cosmos, SQL Server, FHIR response, history, include, and continuation behavior.

**Architecture:** `Ignixa.Search` owns production parsing, binding, control parameters, and typed search expressions. FHIR Server keeps its existing `SearchOptions` as a temporary execution envelope, lowers Ignixa expressions through one explicit Cosmos compatibility bridge, and orchestrates `Ignixa.Search.Sql` through the existing SQL retry, routing, hydration, and Bundle pipeline. The legacy parser and SQL renderer remain available behind explicit routing until differential validation proves parity.

**Tech Stack:** .NET 10, C#, xUnit, NSubstitute, `Ignixa.Search`, `Ignixa.Search.Sql`, Microsoft.Data.SqlClient, SQL Server schema migrations, Cosmos DB query generation, existing FHIR Server feature flags and telemetry.

---

## Scope and execution gates

This is one combined plan because parser, Cosmos, SQL compilation, schema, writes, and continuation behavior must be validated as one search contract. Each task produces a testable increment, but the compiled SQL path must not be enabled for production traffic until Gate 4 and the differential gates pass. The initial integration uses the currently published `Ignixa.Search` 0.6.28 and `Ignixa.Search.Sql` 0.6.28-alpha packages; gaps fixed by PR #353 are tracked as explicit unsupported shapes and upstream-upgrade gates rather than silently approximated in FHIR Server.

The implementation starts from the current `main`-based branch. Pin the currently published Ignixa package versions first, then upgrade to the package containing PR #353 when it is available. If a public production API is missing, change it in the Ignixa repository first, publish a versioned package, and then update this branch; do not add a second SQL renderer or copy Ignixa semantic types into FHIR Server.

## File map

### Dependency and parser boundary

- Modify `Directory.Packages.props` to pin exact `Ignixa.Search` and `Ignixa.Search.Sql` package versions.
- Modify `src/Microsoft.Health.Fhir.Core/Microsoft.Health.Fhir.Core.csproj` to reference `Ignixa.Search`.
- Modify `src/Microsoft.Health.Fhir.R4.Core/Microsoft.Health.Fhir.R4.Core.csproj`, `src/Microsoft.Health.Fhir.R4B.Core/Microsoft.Health.Fhir.R4B.Core.csproj`, `src/Microsoft.Health.Fhir.R5.Core/Microsoft.Health.Fhir.R5.Core.csproj`, and `src/Microsoft.Health.Fhir.Stu3.Core/Microsoft.Health.Fhir.Stu3.Core.csproj` to make `Ignixa.Search` available to the shared core items.
- Modify `src/Microsoft.Health.Fhir.R4.Api/Microsoft.Health.Fhir.R4.Api.csproj`, `src/Microsoft.Health.Fhir.R4B.Api/Microsoft.Health.Fhir.R4B.Api.csproj`, `src/Microsoft.Health.Fhir.R5.Api/Microsoft.Health.Fhir.R5.Api.csproj`, and `src/Microsoft.Health.Fhir.Stu3.Api/Microsoft.Health.Fhir.Stu3.Api.csproj` to make `Ignixa.Search` available to the shared API items.
- Modify `src/Microsoft.Health.Fhir.CosmosDb/Microsoft.Health.Fhir.CosmosDb.csproj` to reference the shared bridge and Ignixa expression assemblies.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Microsoft.Health.Fhir.SqlServer.csproj` to reference `Ignixa.Search.Sql`.
- Create `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaFhirVersionAdapter.cs`.
- Create `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaSearchTenantAccessor.cs`.
- Create `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaSearchOptionsAdapter.cs`.
- Modify `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/SearchOptionsFactory.cs`.
- Modify `src/Microsoft.Health.Fhir.Core/Features/Search/SearchOptions.cs`.
- Modify `src/Microsoft.Health.Fhir.Shared.Api/Modules/SearchModule.cs`.
- Create `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/Ignixa/IgnixaSearchOptionsAdapterTests.cs`.
- Modify `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/SearchOptionsFactoryTests.cs`.

### Cosmos compatibility

- Create `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaLegacyExpressionBridge.cs`.
- Create `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaLegacyExpressionBridgeVisitor.cs`.
- Create `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaSearchValueBridge.cs`.
- Modify `src/Microsoft.Health.Fhir.CosmosDb/Features/Search/FhirCosmosSearchService.cs`.
- Keep `src/Microsoft.Health.Fhir.CosmosDb/Features/Search/Queries/ExpressionQueryBuilder.cs` consuming FHIR Server legacy types until native Ignixa Cosmos support exists.
- Create `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/Ignixa/IgnixaLegacyExpressionBridgeTests.cs`.
- Create `src/Microsoft.Health.Fhir.CosmosDb.UnitTests/Features/Search/FhirCosmosSearchServiceTests.cs`.
- Create `src/Microsoft.Health.Fhir.CosmosDb.UnitTests/Features/Search/Queries/ExpressionQueryBuilderTests.cs`.

### SQL compiler and catalog

- Create `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlSymbolResolver.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlCompilerAdapter.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaCompiledSearchResult.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaCompiledContinuation.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlCapabilityRouter.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Registration/FhirServerBuilderSqlServerRegistrationExtensions.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Search/SqlServerSearchService.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Search/ContinuationToken.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Search/IncludesContinuationToken.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaSqlSymbolResolverTests.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaSqlCompilerAdapterTests.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaCompiledContinuationTests.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaSqlSchemaCompatibilityTests.cs`.

### Schema, writes, and validation

- Verify `src/Microsoft.Health.Fhir.SqlServer/Features/Schema/Migrations/116.diff.sql` against the Ignixa compiler catalog.
- Create `src/Microsoft.Health.Fhir.SqlServer/Features/Schema/Migrations/117.diff.sql` if the catalog alignment requires a schema change.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/SearchParameterRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/CompositeSearchParameterRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/SearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/CompositeSearchParamRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/QuantitySearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/ReferenceSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/DateTimeSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/NumberSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/StringSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/UriSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenTextListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/ReferenceTokenCompositeSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenDateTimeCompositeSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenNumberNumberCompositeSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenQuantityCompositeSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenStringCompositeSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenTokenCompositeSearchParamListRowGenerator.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/Registry/SqlServerSearchParameterStatusDataStore.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/Registry/SqlServerResourceSearchParameterStatus.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/Registry/SearchParameterStatusCollection.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/SqlServerFhirModel.cs`.
- Modify `src/Microsoft.Health.Fhir.Core/Features/Operations/Reindex/ReindexProcessingJob.cs`.
- Modify `src/Microsoft.Health.Fhir.Core/Features/Operations/Reindex/ReindexOrchestratorJob.cs`.
- Modify `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Reindex/ReindexSingleResourceRequestHandler.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaSqlWriteContractTests.cs`.
- Create `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaDifferentialSearchTests.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/SqlServerSearchServiceTests.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/ContinuationTokenTests.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/IncludesContinuationTokenTests.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/SqlQueryGeneratorTests.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Storage/TvpRowGeneration/SearchParamListRowGeneratorTests.cs`.
- Modify `src/Microsoft.Health.Fhir.Core/Configs/CoreFeatureConfiguration.cs`.
- Modify `src/Microsoft.Health.Fhir.Core/Logging/Metrics/Handlers/DefaultSearchMetricHandler.cs`.
- Modify `src/Microsoft.Health.Fhir.SqlServer/Registration/FhirSqlServerConfiguration.cs`.

## Task 1: Pin the Ignixa libraries and establish the upstream contract

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/Microsoft.Health.Fhir.Core/Microsoft.Health.Fhir.Core.csproj`
- Modify: `src/Microsoft.Health.Fhir.R4.Core/Microsoft.Health.Fhir.R4.Core.csproj`
- Modify: `src/Microsoft.Health.Fhir.R4B.Core/Microsoft.Health.Fhir.R4B.Core.csproj`
- Modify: `src/Microsoft.Health.Fhir.R5.Core/Microsoft.Health.Fhir.R5.Core.csproj`
- Modify: `src/Microsoft.Health.Fhir.Stu3.Core/Microsoft.Health.Fhir.Stu3.Core.csproj`
- Modify: `src/Microsoft.Health.Fhir.R4.Api/Microsoft.Health.Fhir.R4.Api.csproj`
- Modify: `src/Microsoft.Health.Fhir.R4B.Api/Microsoft.Health.Fhir.R4B.Api.csproj`
- Modify: `src/Microsoft.Health.Fhir.R5.Api/Microsoft.Health.Fhir.R5.Api.csproj`
- Modify: `src/Microsoft.Health.Fhir.Stu3.Api/Microsoft.Health.Fhir.Stu3.Api.csproj`
- Modify: `src/Microsoft.Health.Fhir.CosmosDb/Microsoft.Health.Fhir.CosmosDb.csproj`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Microsoft.Health.Fhir.SqlServer.csproj`
- Test: `src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj`
- Test: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Microsoft.Health.Fhir.SqlServer.UnitTests.csproj`

- [ ] **Step 1: Record the required Ignixa API contract before changing FHIR Server**

The initial package baseline is `Ignixa.Search` 0.6.28 and `Ignixa.Search.Sql` 0.6.28-alpha. Verify that baseline exposes these production-facing members without FHIR Server reaching into internal types:

```csharp
using Ignixa.Abstractions;
using Ignixa.Search.Models;
using Ignixa.Search.Parsing;
using Ignixa.Search.Sql.Ast;
using Ignixa.Search.Sql.Builders;

public interface ISearchOptionsBuilderFactory
{
    ISearchOptionsBuilder Create(FhirVersion fhirVersion, int? tenantId);
}

public interface ISearchOptionsBuilder
{
    SearchOptions Build(
        string? resourceType,
        IReadOnlyList<QueryParameter> parameters,
        ISchema? schemaProvider = null,
        IList<ParameterTrace>? outcomes = null);
}

public sealed record EmittedSql(
    string Sql,
    IReadOnlyList<EmittedSqlParameter> Parameters,
    IReadOnlyList<SqlTextRange>? TextRanges = null);
```

The exact member names must match the published Ignixa package. `QueryPlan` supplies result-shape metadata through `Includes` and `CountOnly`; the adapter records the package assembly version and FHIR Server schema version alongside the emitted result. Any missing PR #353 behavior or public lowering input is recorded as an unsupported capability and routed to legacy; FHIR Server must not edit emitted SQL or reimplement the missing compiler behavior. Upgrade the package and remove each capability gate only after the upstream version is published and its contract tests pass.

- [ ] **Step 2: Add exact, centrally managed package versions**

Add the exact published baseline package versions `0.6.28` for `Ignixa.Search` and `0.6.28-alpha` for `Ignixa.Search.Sql`. Do not use floating versions, branch URLs, or a source project reference in the committed FHIR Server build. Upgrade both entries together when the package containing PR #353 is published:

```xml
<ItemGroup>
  <PackageVersion Include="Ignixa.Search" Version="$(IgnixaSearchPackageVersion)" />
  <PackageVersion Include="Ignixa.Search.Sql" Version="$(IgnixaSearchSqlPackageVersion)" />
</ItemGroup>
```

Use separate central properties so the baseline's package versions remain explicit:

```xml
<PropertyGroup>
  <IgnixaSearchPackageVersion>0.6.28</IgnixaSearchPackageVersion>
  <IgnixaSearchSqlPackageVersion>0.6.28-alpha</IgnixaSearchSqlPackageVersion>
</PropertyGroup>
```

The committed branch must contain real released or prerelease numbers; update both properties together when an upstream package containing PR #353 is available.

Add explicit `PackageReference` entries to all listed production project files. Keep `Ignixa.Search` explicit even though `Ignixa.Search.Sql` depends on it so package restore cannot silently select a different semantic library version.

- [ ] **Step 3: Restore and build the dependency-only change**

Run:

```powershell
dotnet restore .\Microsoft.Health.Fhir.sln
dotnet build .\Microsoft.Health.Fhir.sln --no-restore --configuration Debug --no-incremental
```

Expected: restore resolves `Ignixa.Search` 0.6.28 and `Ignixa.Search.Sql` 0.6.28-alpha for `net10.0`, and the solution builds without any source changes. If the baseline lacks a required public member, record the gap and keep that capability disabled rather than introducing a local renderer.

- [ ] **Step 4: Commit the dependency boundary**

```powershell
git add Directory.Packages.props src\Microsoft.Health.Fhir.Core\Microsoft.Health.Fhir.Core.csproj src\Microsoft.Health.Fhir.R4.Core\Microsoft.Health.Fhir.R4.Core.csproj src\Microsoft.Health.Fhir.R4B.Core\Microsoft.Health.Fhir.R4B.Core.csproj src\Microsoft.Health.Fhir.R5.Core\Microsoft.Health.Fhir.R5.Core.csproj src\Microsoft.Health.Fhir.Stu3.Core\Microsoft.Health.Fhir.Stu3.Core.csproj src\Microsoft.Health.Fhir.R4.Api\Microsoft.Health.Fhir.R4.Api.csproj src\Microsoft.Health.Fhir.R4B.Api\Microsoft.Health.Fhir.R4B.Api.csproj src\Microsoft.Health.Fhir.R5.Api\Microsoft.Health.Fhir.R5.Api.csproj src\Microsoft.Health.Fhir.Stu3.Api\Microsoft.Health.Fhir.Stu3.Api.csproj src\Microsoft.Health.Fhir.CosmosDb\Microsoft.Health.Fhir.CosmosDb.csproj src\Microsoft.Health.Fhir.SqlServer\Microsoft.Health.Fhir.SqlServer.csproj src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj src\Microsoft.Health.Fhir.SqlServer.UnitTests\Microsoft.Health.Fhir.SqlServer.UnitTests.csproj
git commit -m "build: pin Ignixa search libraries"
```

## Task 2: Make Ignixa the canonical parser while retaining the FHIR Server execution envelope

**Files:**
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaFhirVersionAdapter.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaSearchTenantAccessor.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaSearchOptionsAdapter.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Search/SearchOptions.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/SearchOptionsFactory.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/SearchModule.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/Ignixa/IgnixaSearchOptionsAdapterTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/SearchOptionsFactoryTests.cs`

- [ ] **Step 1: Add the FHIR-version adapter**

Create one version-specific mapping with no string parsing:

```csharp
internal static class IgnixaFhirVersionAdapter
{
    internal static Ignixa.Abstractions.FhirVersion Current
    {
        get
        {
#if Stu3
            return Ignixa.Abstractions.FhirVersion.Stu3;
#elif R4
            return Ignixa.Abstractions.FhirVersion.R4;
#elif R4B
            return Ignixa.Abstractions.FhirVersion.R4B;
#elif R5
            return Ignixa.Abstractions.FhirVersion.R5;
#else
            throw new InvalidOperationException("No FHIR version compilation symbol is configured.");
#endif
        }
    }
}
```

Add tests compiled in each version-specific test project that assert the mapped enum is `Stu3`, `R4`, `R4B`, or `R5` respectively.

- [ ] **Step 2: Add the tenant accessor with an explicit request-context key**

Create `IgnixaSearchTenantAccessor` that reads `int?` from `IFhirRequestContext.Properties` under a single constant key, returns `null` when no tenant is configured, and throws `InvalidOperationException` when the property exists with a non-integer value. This prevents a malformed tenant identifier from silently selecting base definitions:

```csharp
internal static class IgnixaSearchContextPropertyNames
{
    internal const string TenantId = "Ignixa.Search.TenantId";
}
```

Add the hosting/tenant middleware assignment at the existing tenant boundary; do not derive the tenant from a URL string in the search factory.

- [ ] **Step 3: Add the Ignixa options adapter**

Create an adapter that converts the already decoded FHIR query tuples to `Ignixa.Search.Parsing.QueryParameter` values, invokes `ISearchOptionsBuilderFactory.Create(IgnixaFhirVersionAdapter.Current, tenantId)`, and returns the builder result. Do not re-encode query values into a query string and do not invoke the legacy `IExpressionParser`.

The adapter must preserve repeated parameters in original order and expose the Ignixa `SearchOptions` result unchanged:

```csharp
internal interface IIgnixaSearchOptionsAdapter
{
    Ignixa.Search.Models.SearchOptions Build(
        string resourceType,
        IReadOnlyList<Tuple<string, string>> queryParameters,
        int? tenantId);
}
```

Use the upstream `QueryParameter(string Name, string Value)` constructor. Reject null parameter names or values before calling Ignixa and let Ignixa's structured parser errors flow to the existing FHIR error mapper. For compartment endpoints, append the existing compartment/smart-compartment constraint to the Ignixa expression after `Build` using Ignixa's `CompartmentExpression` shape; do not invoke the legacy parser to create that constraint.

- [ ] **Step 4: Preserve server-only controls in `SearchOptions`**

Add an internal `IgnixaOptions` property to `src/Microsoft.Health.Fhir.Core/Features/Search/SearchOptions.cs` and copy it in the cloning constructor. Keep these server-only properties in the envelope: `ResourceVersionTypes`, `OnlyIds`, `FeedRange`, `QueryHints`, include-operation state, async-operation state, access-control state, and legacy continuation metadata.

The canonical fields must be read from `IgnixaOptions` after this task:

| Behavior | Ignixa source |
|---|---|
| Expression | `IgnixaOptions.Expression` |
| Sort | `IgnixaOptions.Sort` |
| `_count` | `IgnixaOptions.MaxItemCount` |
| `_total` | `IgnixaOptions.Total` |
| `_summary=count` | `IgnixaOptions.Summary == SummaryType.Count` |
| `_include`/`_revinclude` | Ignixa include collections |
| Unsupported parameters | Ignixa issue/unsupported collection |

The adapter must map these fields to the existing envelope only where existing downstream code still requires a FHIR Server type. It must not invoke the legacy parser to populate a second semantic expression.

- [ ] **Step 5: Route `SearchOptionsFactory` through the adapter**

Keep the existing loop that handles FHIR Server-only routing controls such as continuation decoding, feed ranges, query hints, include continuation, and history/deleted flags. Validate `_type` locally but pass `_type`, `_count`, `_total`, `_summary`, `_sort`, `_include`, `_revinclude`, `_elements`, and all ordinary search parameters unchanged to `IIgnixaSearchOptionsAdapter`; those values are Ignixa-owned semantics. Pass only server-only values to the local compatibility logic, then assign `searchOptions.IgnixaOptions`.

Replace the production assignment from:

```csharp
searchOptions.Expression = _expressionParser.Parse(...);
```

with:

```csharp
searchOptions.IgnixaOptions = _ignixaSearchOptionsAdapter.Build(
    resourceType,
    searchParameterTuples,
    _ignixaSearchTenantAccessor.GetTenantId(_contextAccessor.RequestContext));
```

Set `Expression`, `SearchParameters`, `Sort`, `UnsupportedSearchParams`, and count/total compatibility values from the Ignixa result through dedicated mapping methods. Populate the legacy `Expression` property with the Task 3 lowerer/bridge projection, not by reparsing the query. Implement the Task 3 bridge before enabling this projection. Retain `_expressionParser` only under a test-oracle constructor path or a differential test helper; it must not be used by normal request creation.

- [ ] **Step 6: Register the adapter and test parser parity**

Register `ISearchOptionsBuilderFactory` from the Ignixa package, `IIgnixaSearchOptionsAdapter`, and `IgnixaSearchTenantAccessor` in `SearchModule.cs`. Keep `IExpressionParser` registered for the differential oracle until the retirement task.

Add tests for STU3, R4, R4B, and R5 covering:

```csharp
[Theory]
[InlineData("_count", "10")]
[InlineData("_total", "accurate")]
[InlineData("_summary", "count")]
[InlineData("_include", "Observation:subject")]
public void Build_UsesIgnixaOptionsForControlParameter(string name, string value)
{
    SearchOptions actual = CreateOptions(Tuple.Create(name, value));

    Assert.NotNull(actual.IgnixaOptions);
    Assert.Equal(ExpectedIgnixaValue(name, value), ReadIgnixaValue(actual.IgnixaOptions, name));
}
```

Add differential cases that compare legacy and Ignixa outcomes for ordinary tokens, qualified tokens, quantities, URI hierarchy, references, composites, chains, `_type`, invalid controls, unsupported parameters, and OperationOutcome issues. A mismatch is a failing test, not a warning.

- [ ] **Step 7: Run parser tests and commit Gate 1**

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Microsoft.Health.Fhir.Shared.Core.UnitTests.csproj --configuration Debug --filter "FullyQualifiedName~SearchOptionsFactoryTests|FullyQualifiedName~IgnixaSearchOptionsAdapterTests"
```

Expected: all existing control-parameter tests and new Ignixa parity tests pass for every compiled FHIR version.

```powershell
git add src\Microsoft.Health.Fhir.Shared.Core\Features\Search\Ignixa src\Microsoft.Health.Fhir.Core\Features\Search\SearchOptions.cs src\Microsoft.Health.Fhir.Shared.Core\Features\Search\SearchOptionsFactory.cs src\Microsoft.Health.Fhir.Shared.Api\Modules\SearchModule.cs src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\Ignixa src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\SearchOptionsFactoryTests.cs
git commit -m "feat: route search parsing through Ignixa"
```

## Task 3: Lower Ignixa expressions through the Cosmos compatibility bridge

**Files:**
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaLegacyExpressionBridge.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaLegacyExpressionBridgeVisitor.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaSearchValueBridge.cs`
- Modify: `src/Microsoft.Health.Fhir.CosmosDb/Features/Search/FhirCosmosSearchService.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/Ignixa/IgnixaLegacyExpressionBridgeTests.cs`
- Create: `src/Microsoft.Health.Fhir.CosmosDb.UnitTests/Features/Search/FhirCosmosSearchServiceTests.cs`
- Create: `src/Microsoft.Health.Fhir.CosmosDb.UnitTests/Features/Search/Queries/ExpressionQueryBuilderTests.cs`

- [ ] **Step 1: Define a structured bridge failure**

Create an internal exception or result type that includes the Ignixa node type, search parameter code, and reason. The bridge must fail before Cosmos query execution when a node cannot be represented:

```csharp
internal sealed class IgnixaExpressionBridgeException : SearchOperationNotSupportedException
{
    internal IgnixaExpressionBridgeException(string nodeType, string parameterCode, string reason)
        : base($"Ignixa expression node '{nodeType}' for parameter '{parameterCode}' cannot be lowered for Cosmos: {reason}")
    {
        NodeType = nodeType;
        ParameterCode = parameterCode;
    }

    internal string NodeType { get; }
    internal string ParameterCode { get; }
}
```

- [ ] **Step 2: Implement the structural visitor**

Implement `IgnixaLegacyExpressionBridgeVisitor` with exhaustive handling for the Ignixa lowered legacy nodes used by the parser: search parameter, missing search parameter, binary, chained, missing field, not, multiary, union, string, compartment, smart compartment, include, sort, `In`, and not-referenced nodes.

For every node:

- preserve `And`/`Or` grouping and operand order;
- preserve chain direction and target resource types;
- preserve include and reverse-include mode plus iterative state;
- preserve missing/not semantics without applying De Morgan transformations;
- preserve composite component order;
- throw `IgnixaExpressionBridgeException` for an unsupported node instead of returning an empty expression.

- [ ] **Step 3: Implement typed search-value conversion**

Map every lowered Ignixa value to the existing FHIR Server search value type without changing normalization:

| Ignixa value | FHIR Server target |
|---|---|
| string/text | `StringSearchValue` |
| token/system/code | `TokenSearchValue` |
| reference | `ReferenceSearchValue` |
| date/date-time bounds | `DateTimeSearchValue` |
| number | `NumberSearchValue` |
| quantity/code/system | `QuantitySearchValue` |
| URI/canonical hierarchy | `UriSearchValue` |

Use explicit switch expressions and include the original parameter code in failures. Do not call the old parser to reconstruct a value from text.

- [ ] **Step 4: Wire Cosmos to the bridge**

In `FhirCosmosSearchService`, replace the production use of `searchOptions.Expression` with:

```csharp
Ignixa.Search.Expressions.Expression lowered =
    Ignixa.Search.Expressions.LegacyExpressionLowerer.LowerToLegacy(searchOptions.IgnixaOptions.Expression);

Expression cosmosExpression =
    _ignixaLegacyExpressionBridge.Convert(lowered);
```

Pass `cosmosExpression` to the existing `ExpressionQueryBuilder`, chained-query handling, continuation handling, and resource materialization. Keep the old expression property only for code paths that have not yet moved to `IgnixaOptions`, and assert that both representations cannot diverge.

- [ ] **Step 5: Add bridge and Cosmos differential tests**

Create structural tests that build Ignixa expressions directly and assert the resulting FHIR Server expression tree node-by-node. Include:

- nested `And`/`Or` with `Not`;
- missing search parameters and missing fields;
- token, quantity, reference, date, number, string, URI, and composite values;
- chained and reverse-chained expressions;
- `_include`, `_revinclude`, and `:iterate`;
- unsupported node failure metadata.

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Microsoft.Health.Fhir.Shared.Core.UnitTests.csproj --configuration Debug --filter FullyQualifiedName~IgnixaLegacyExpressionBridgeTests
dotnet test .\src\Microsoft.Health.Fhir.CosmosDb.UnitTests\Microsoft.Health.Fhir.CosmosDb.UnitTests.csproj --configuration Debug --filter "FullyQualifiedName~FhirCosmosSearchServiceTests|FullyQualifiedName~ExpressionQueryBuilderTests"
```

Expected: Cosmos results, errors, ordering, and continuation sequences match the legacy parser oracle on the same fixtures.

- [ ] **Step 6: Commit Gate 2**

```powershell
git add src\Microsoft.Health.Fhir.Shared.Core\Features\Search\Ignixa src\Microsoft.Health.Fhir.CosmosDb\Features\Search\FhirCosmosSearchService.cs src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\Ignixa src\Microsoft.Health.Fhir.CosmosDb.UnitTests\Features\Search
git commit -m "feat: bridge Ignixa expressions to Cosmos"
```

## Task 4: Implement SQL symbol resolution against the FHIR Server catalog

**Files:**
- Create: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlSymbolResolver.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlSymbolLookup.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Registration/FhirServerBuilderSqlServerRegistrationExtensions.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaSqlSymbolResolverTests.cs`

- [ ] **Step 1: Define the resolver boundary**

Implement `Ignixa.Search.Sql.Symbols.ISymbolResolver` as a request-scoped service. All database reads happen in `Resolve`; `Lower` and `SqlBuilder` receive only resolved symbols.

The resolver must:

- resolve resource type IDs from `ResourceType` using the existing `ISqlServerFhirModel`;
- resolve search parameter IDs using canonical URL, override URL, code, type, and resource scope;
- resolve token-system IDs with one set-based lookup per request;
- resolve quantity-code IDs with one set-based lookup per request;
- cache repeated lookups within one request;
- return the Ignixa contract's unresolved result for a missing catalog row;
- never substitute a different definition when a URL override is requested.

- [ ] **Step 2: Add set-based lookup helpers**

Create `IgnixaSqlSymbolLookup` with methods equivalent to:

```csharp
Task<IReadOnlyDictionary<string, short>> ResolveResourceTypesAsync(
    IReadOnlyCollection<string> resourceTypes,
    CancellationToken cancellationToken);

Task<IReadOnlyDictionary<SearchParameterIdentity, short>> ResolveSearchParametersAsync(
    IReadOnlyCollection<SearchParameterIdentity> definitions,
    CancellationToken cancellationToken);

Task<IReadOnlyDictionary<string, int>> ResolveTokenSystemsAsync(
    IReadOnlyCollection<string> systems,
    CancellationToken cancellationToken);

Task<IReadOnlyDictionary<QuantityCodeIdentity, int>> ResolveQuantityCodesAsync(
    IReadOnlyCollection<QuantityCodeIdentity> codes,
    CancellationToken cancellationToken);
```

Use existing SQL command/retry extensions and parameterized TVPs or batched parameters. The lookup helpers must not interpolate user strings into SQL.

- [ ] **Step 3: Register the resolver**

Register `IgnixaSqlSymbolLookup` as scoped, `IgnixaSqlSymbolResolver` as scoped, and expose it to the compiler adapter in `FhirServerBuilderSqlServerRegistrationExtensions.cs`. Do not register a singleton containing request-scoped lookup state.

- [ ] **Step 4: Test resolution and failure classification**

Use NSubstitute to assert:

- repeated symbol requests result in one backing lookup;
- system and quantity lookups are batched;
- canonical URL and override URL remain distinct;
- missing definitions are returned as unresolved, not mapped to ID zero;
- cancellation and SQL exceptions propagate through `ISqlRetryService`;
- a mismatched resource scope is not accepted.

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.SqlServer.UnitTests\Microsoft.Health.Fhir.SqlServer.UnitTests.csproj --configuration Debug --filter FullyQualifiedName~IgnixaSqlSymbolResolverTests
```

- [ ] **Step 5: Commit symbol resolution**

```powershell
git add src\Microsoft.Health.Fhir.SqlServer\Features\Search\Ignixa src\Microsoft.Health.Fhir.SqlServer\Registration\FhirServerBuilderSqlServerRegistrationExtensions.cs src\Microsoft.Health.Fhir.SqlServer.UnitTests\Features\Search\Ignixa
git commit -m "feat: resolve Ignixa SQL symbols from FHIR catalog"
```

## Task 5: Verify the Ignixa catalog against schema 116 and fix the contract upstream or in the schema

**Files:**
- Verify: `src/Microsoft.Health.Fhir.SqlServer/Features/Schema/Migrations/116.diff.sql`
- Verify: `src/Microsoft.Health.Fhir.SqlServer/Microsoft.Health.Fhir.SqlServer.csproj`
- Create: `src/Microsoft.Health.Fhir.SqlServer/Features/Schema/Migrations/117.diff.sql` only when the compatibility test identifies a required physical change.
- Create: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaSqlSchemaCompatibilityTests.cs`

- [ ] **Step 1: Enumerate every compiler-referenced object**

Build a test-side catalog manifest from the public Ignixa catalog contract and compare it with schema 116. The manifest must cover:

```text
Resource, ResourceType, ResourceSurrogateId, ResourceVersion, IsHistory, IsDeleted,
StringSearchParam, TokenSearchParam, ReferenceSearchParam, DateTimeSearchParam,
NumberSearchParam, QuantitySearchParam, UriSearchParam,
CompositeSearchParam, TokenTextSearchParam, SearchParameterStatus,
System, QuantityCode, overflow columns, collation, nullability, and key columns.
```

The test must fail with the exact missing table, column, type, length, collation, or key mismatch.

- [ ] **Step 2: Regenerate or update the Ignixa catalog at the source**

If schema 116 is compatible, publish the Ignixa package with the catalog generated from the verified DDL and record the catalog/schema identity used by the compiler. If it is incompatible, update the Ignixa `AdditionalFiles` schema input and generator in `brendankowitz/ignixa-fhir`, publish a new package, and create the next FHIR Server migration with matching write-path changes. Do not accept the existing Ignixa `97.sql` snapshot as authoritative.

- [ ] **Step 3: Add live schema validation**

Add an integration test that connects to the SQL test database, runs the manifest against `sys.tables`, `sys.columns`, `sys.types`, `sys.index_columns`, and collation metadata, and asserts schema version 116 (or the new migration version when required).

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.SqlServer.UnitTests\Microsoft.Health.Fhir.SqlServer.UnitTests.csproj --configuration Debug --filter FullyQualifiedName~IgnixaSqlSchemaCompatibilityTests
```

Expected: the test passes against a database initialized from the embedded latest migration and fails health validation before compiled execution when any object differs.

- [ ] **Step 4: Commit the catalog gate**

```powershell
git add src\Microsoft.Health.Fhir.SqlServer.UnitTests\Features\Search\Ignixa src\Microsoft.Health.Fhir.SqlServer\Features\Schema\Migrations src\Microsoft.Health.Fhir.SqlServer\Microsoft.Health.Fhir.SqlServer.csproj
git commit -m "test: verify Ignixa SQL catalog against schema"
```

## Task 6: Orchestrate Resolve, Lower, and SqlBuilder without adding a renderer

**Files:**
- Create: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaCompiledSearchResult.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlCompilerAdapter.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlCapabilityRouter.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Registration/FhirServerBuilderSqlServerRegistrationExtensions.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaSqlCompilerAdapterTests.cs`

- [ ] **Step 1: Define the compiled result contract**

Create a FHIR Server adapter result that preserves the public Ignixa result and adds only execution metadata:

```csharp
internal sealed record IgnixaCompiledSearchResult(
    string Sql,
    IReadOnlyList<Ignixa.Search.Sql.Builders.EmittedSqlParameter> Parameters,
    Ignixa.Search.Sql.Ast.QueryPlan Plan,
    bool HasIncludes,
    bool CountOnly,
    string CompilerVersion,
    string SchemaVersion,
    Ignixa.Search.Sql.Symbols.ResolvedSymbols ResolvedSymbols);
```

Set `HasIncludes` from `QueryPlan.Includes` and `CountOnly` from `QueryPlan.CountOnly`. Preserve the public `ResolvedSymbols` result from `Resolve.RunAsync`; its `Unresolved` collection supplies unresolved definitions for diagnostics. The adapter must not modify emitted SQL text. Convert each `EmittedSqlParameter` to a `Microsoft.Data.SqlClient.SqlParameter` only at command-binding time.

- [ ] **Step 2: Implement the compiler adapter**

Implement:

```csharp
Task<IgnixaCompiledSearchResult> CompileAsync(
    SearchOptions options,
    string resourceType,
    CancellationToken cancellationToken);
```

The method must:

1. require `options.IgnixaOptions`;
2. call `Resolve.RunAsync` with `IgnixaSqlSymbolResolver`;
3. classify unresolved symbols before SQL execution;
4. call `Lower.Run` with `targetResourceType`, includes, reverse-includes, include limit, sort, sort phase, page, count-only, top, and approximation-reference-time arguments;
5. pass resource-version, deleted, access-control, and feed-range predicates through the public Ignixa lowering options/outer-predicate contract established in Task 1; FHIR Server must not append them to emitted SQL;
6. call `SqlBuilder.Run`;
7. return emitted SQL, typed parameters, plan, result shape, compiler identity, and schema identity.

No stage after `Resolve` may perform database I/O. No FHIR Server code may append predicates, rewrite CTEs, or render SQL.

- [ ] **Step 3: Implement capability routing**

Create `IgnixaSqlCapabilityRouter` with three explicit decisions:

```csharp
internal enum SearchEngineRoute
{
    Legacy,
    Compiled,
    Shadow
}
```

Route to `Legacy` when the query uses an unsupported compiler capability, missing sort behavior not represented in the plan, unsupported history/deleted/access-control mode, an invalid schema identity, or a legacy continuation token. Route to `Compiled` only when all physical filters are in the plan. Route to `Shadow` when the feature flag requests sampled comparison.

Return a structured route reason containing capability name and search parameter code. Do not catch arbitrary exceptions and return `Legacy`.

- [ ] **Step 4: Register and unit-test the orchestration**

Register the adapter and router as scoped services. Test stage order with substitutes, typed parameter preservation, count-only and include result shapes, unresolved-symbol classification, compiler/schema identity propagation, and capability fallback.

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.SqlServer.UnitTests\Microsoft.Health.Fhir.SqlServer.UnitTests.csproj --configuration Debug --filter FullyQualifiedName~IgnixaSqlCompilerAdapterTests
```

- [ ] **Step 5: Commit Gate 3**

```powershell
git add src\Microsoft.Health.Fhir.SqlServer\Features\Search\Ignixa src\Microsoft.Health.Fhir.SqlServer\Registration\FhirServerBuilderSqlServerRegistrationExtensions.cs src\Microsoft.Health.Fhir.SqlServer.UnitTests\Features\Search\Ignixa
git commit -m "feat: compile FHIR search with Ignixa SQL"
```

## Task 7: Execute compiled SQL through the existing SQL Server search shell

**Files:**
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/SqlServerSearchService.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaCompiledSearchResult.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlResultReader.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaResourceKeyHydrator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/SqlServerSearchServiceTests.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaSqlResultReaderTests.cs`

- [ ] **Step 1: Add the compiler branch without deleting the legacy branch**

At the beginning of `SqlServerSearchService.SearchAsync`, select the engine with `IgnixaSqlCapabilityRouter`. Keep include two-phase orchestration, retry policy, read-replica routing, query timeout, query-plan reuse policy, and logging in `SqlServerSearchService`.

The compiled branch must be isolated in a method with this shape:

```csharp
private async Task<SearchResult> RunCompiledSearchAsync(
    SqlSearchOptions options,
    CancellationToken cancellationToken)
{
    IgnixaCompiledSearchResult compiled = await _ignixaSqlCompilerAdapter.CompileAsync(
        options,
        options.ResourceType,
        cancellationToken);

    return await _ignixaSqlResultReader.ExecuteAndHydrateAsync(
        compiled,
        options,
        cancellationToken);
}
```

Use `SqlCommand.CommandText = compiled.Sql`, add every typed parameter from `compiled.Parameters`, and execute with the existing `ExecuteReaderAsync` retry extension. Never concatenate resource types, parameter values, IDs, or continuation values into the SQL text.

- [ ] **Step 2: Validate and read the result shape**

Implement `IgnixaSqlResultReader` for exactly three shapes:

1. ordered `(ResourceTypeId, ResourceSurrogateId)` rows;
2. ordered key rows plus `IsMatch` and `IsPartial` for includes;
3. count-only scalar/row result.

Reject a shape that does not match the requested operation with a structured operational error. Do not interpret an unexpected empty result as a successful count.

- [ ] **Step 3: Hydrate in compiler order**

Implement `IgnixaResourceKeyHydrator` using existing resource projection and decompression services. Remove duplicate keys while retaining first-seen order, group only for the SQL read if required, then restore the compiler order before returning `SearchResultEntry` values.

Apply `ResourceVersionTypes`, access-control predicates, feed range, and query hints in the compiled plan before paging. Do not filter or sort a fully paged key list in memory.

- [ ] **Step 4: Preserve totals and count-only**

Map count-only output to the existing Bundle count path. For accurate totals, execute the compiler's count shape or approved count plan separately through the same symbol/plan contract. Do not infer totals from page length.

- [ ] **Step 5: Add result and service tests**

Test:

- key order survives hydration;
- duplicate keys are removed only after first-seen order is recorded;
- include metadata reaches `SearchResultEntry`;
- count-only returns the exact scalar;
- typed parameters are attached to the command;
- cancellation and SQL failures propagate;
- resource-version and access-control filters are never applied after paging.

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.SqlServer.UnitTests\Microsoft.Health.Fhir.SqlServer.UnitTests.csproj --configuration Debug --filter "FullyQualifiedName~SqlServerSearchServiceTests|FullyQualifiedName~IgnixaSqlResultReaderTests"
```

- [ ] **Step 6: Commit compiled read execution**

```powershell
git add src\Microsoft.Health.Fhir.SqlServer\Features\Search\SqlServerSearchService.cs src\Microsoft.Health.Fhir.SqlServer\Features\Search\Ignixa src\Microsoft.Health.Fhir.SqlServer.UnitTests\Features\Search\SqlServerSearchServiceTests.cs src\Microsoft.Health.Fhir.SqlServer.UnitTests\Features\Search\Ignixa
git commit -m "feat: execute and hydrate Ignixa SQL results"
```

## Task 8: Version continuation tokens and preserve includes

**Files:**
- Create: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaCompiledContinuation.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/ContinuationToken.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/IncludesContinuationToken.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/SqlServerSearchService.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaCompiledContinuationTests.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/ContinuationTokenTests.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/IncludesContinuationTokenTests.cs`

- [ ] **Step 1: Define the compiled continuation payload**

Serialize a versioned payload containing:

```csharp
internal sealed record IgnixaCompiledContinuation(
    string Engine,
    string CompilerVersion,
    string SchemaVersion,
    IReadOnlyList<string> ResourceTypes,
    IReadOnlyList<string> SortDefinitions,
    IReadOnlyList<object> LastSortTuple,
    short LastResourceTypeId,
    long LastResourceSurrogateId,
    string IncludePhase,
    string SecondPhaseContinuation);
```

Use a distinct `Engine = "IgnixaSql"` discriminator and a schema/version field. Preserve the existing JSON array token format for legacy tokens and make token parsing explicit rather than relying on array length alone.

- [ ] **Step 2: Route tokens without reinterpretation**

When a request contains a legacy token, route to the legacy engine. When it contains an `IgnixaSql` token, validate compiler version, schema version, resource type set, sort definitions, and tie-breaker before compiling. A mismatch must return the existing bad-request/invalid-continuation response; it must not silently restart from the first page or switch engines.

- [ ] **Step 3: Preserve missing-sort and include phases**

Keep FHIR Server's second-phase missing-sort behavior and separate primary/include continuation state. Include continuation must preserve match resource type/surrogate bounds, include resource bounds, `IsMatch`, `IsPartial`, sort-phase state, and second-phase continuation. Compile each phase with the same engine identity.

- [ ] **Step 4: Test page sequences**

Add tests that:

- round-trip every compiled token field;
- reject legacy tokens on the compiled branch and compiled tokens on the legacy branch;
- reject stale compiler/schema identities;
- preserve stable tie-breaking for equal sort values;
- resume primary results, `_include`, `_revinclude`, and `:iterate` pages with identical sequences;
- preserve second-phase missing-sort continuation.

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.SqlServer.UnitTests\Microsoft.Health.Fhir.SqlServer.UnitTests.csproj --configuration Debug --filter "FullyQualifiedName~IgnixaCompiledContinuationTests|FullyQualifiedName~ContinuationTokenTests|FullyQualifiedName~IncludesContinuationTokenTests"
```

- [ ] **Step 5: Commit continuation compatibility**

```powershell
git add src\Microsoft.Health.Fhir.SqlServer\Features\Search\Ignixa src\Microsoft.Health.Fhir.SqlServer\Features\Search\ContinuationToken.cs src\Microsoft.Health.Fhir.SqlServer\Features\Search\IncludesContinuationToken.cs src\Microsoft.Health.Fhir.SqlServer\Features\Search\SqlServerSearchService.cs src\Microsoft.Health.Fhir.SqlServer.UnitTests\Features\Search
git commit -m "feat: version compiled search continuations"
```

## Task 9: Align SQL index writes and lookup maintenance with the compiler schema

**Files:**
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/SearchParameterRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/CompositeSearchParameterRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/SearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/CompositeSearchParamRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/QuantitySearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/ReferenceSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/DateTimeSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/NumberSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/StringSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/UriSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenTextListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/ReferenceTokenCompositeSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenDateTimeCompositeSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenNumberNumberCompositeSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenQuantityCompositeSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenStringCompositeSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/TvpRowGeneration/Merge/TokenTokenCompositeSearchParamListRowGenerator.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/Registry/SqlServerSearchParameterStatusDataStore.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/Registry/SqlServerResourceSearchParameterStatus.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/Registry/SearchParameterStatusCollection.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/SqlServerFhirModel.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Storage/SqlServerFhirDataStore.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Operations/Reindex/ReindexProcessingJob.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Operations/Reindex/ReindexOrchestratorJob.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Reindex/ReindexSingleResourceRequestHandler.cs`
- Create: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaSqlWriteContractTests.cs`

- [ ] **Step 1: Compare every index writer to the verified catalog**

For string, token, reference, date, number, quantity, URI, composite, token-text, overflow, and resource rows, assert that the TVP column names, SQL types, lengths, nullability, and lookup IDs match the catalog manifest from Task 5.

Use a table-driven test:

```csharp
[Theory]
[MemberData(nameof(SearchIndexWriteContracts))]
public void SearchIndexWriter_MatchesIgnixaCatalog(
    string tableName,
    string columnName,
    SqlDbType expectedType,
    int? expectedLength)
{
    SearchIndexWriteContract actual = ReadWriterContract(tableName, columnName);

    Assert.Equal(expectedType, actual.SqlType);
    Assert.Equal(expectedLength, actual.Length);
}
```

- [ ] **Step 2: Synchronize system and quantity-code lookup maintenance**

Update the write path so a newly indexed token system or quantity code is inserted/resolved using the same normalization and identity rules used by `IgnixaSqlSymbolResolver`. Keep lookup writes set-based and idempotent. A failed lookup insert must propagate through the existing transaction rather than leaving an index row with an invalid foreign key.

- [ ] **Step 3: Preserve history/deleted and partial-index semantics**

Verify row generators include the resource surrogate, version, deleted, and search-parameter status fields required by compiled predicates. Ensure partial indexing is represented exactly as the compiler expects; do not make a partially indexed resource visible to a compiled query that would be hidden by the legacy path.

- [ ] **Step 4: Add backfill/reindex support**

Update the existing reindex orchestration so it can populate any new catalog columns or lookup IDs. The reindex operation must be resumable, report a schema/compiler identity, and complete before the compiled feature flag is enabled for a tenant.

- [ ] **Step 5: Run write and reindex validation**

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.SqlServer.UnitTests\Microsoft.Health.Fhir.SqlServer.UnitTests.csproj --configuration Debug --filter FullyQualifiedName~IgnixaSqlWriteContractTests
```

Run the shared SQL integration tests through the R4 host project:

```powershell
dotnet test .\test\Microsoft.Health.Fhir.R4.Tests.Integration\Microsoft.Health.Fhir.R4.Tests.Integration.csproj --configuration Debug --filter "FullyQualifiedName~SqlServerSearchServiceIntegrationTests|FullyQualifiedName~ReindexSearchTests"
```

Run against a database initialized from the latest migration. Expected: resources written after migration and resources reindexed from pre-migration data produce identical compiled and legacy key sets.

- [ ] **Step 6: Commit the write contract**

```powershell
git add src\Microsoft.Health.Fhir.SqlServer\Features\Storage src\Microsoft.Health.Fhir.SqlServer\Features\Operations\Reindex src\Microsoft.Health.Fhir.SqlServer.UnitTests\Features\Search\Ignixa
git commit -m "feat: align SQL search writes with Ignixa catalog"
```

## Task 10: Add compile-only shadowing and sampled differential execution

**Files:**
- Create: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaDifferentialSearchTests.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlCapabilityRouter.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/SqlServerSearchService.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Configs/CoreFeatureConfiguration.cs`.
- Modify: `src/Microsoft.Health.Fhir.Core/Logging/Metrics/Handlers/DefaultSearchMetricHandler.cs`.
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Registration/FhirSqlServerConfiguration.cs`.

- [ ] **Step 1: Add explicit engine flags**

Add configuration values with safe defaults:

```text
Search.Ignixa.Parser.Enabled = false
Search.Ignixa.SqlCompiler.Enabled = false
Search.Ignixa.SqlCompiler.ShadowSampleRate = 0
Search.Ignixa.SqlCompiler.AllowFallback = true
Search.Ignixa.SqlCompiler.SchemaVersion = 116
```

The compiled engine remains disabled by default. Flags must be evaluated per request/tenant/resource type without changing continuation tokens.

- [ ] **Step 2: Implement compile-only shadowing**

When parser/compiler shadowing is enabled, build the Ignixa options, resolve symbols, lower, and emit SQL without executing it. Record engine, compiler version, schema version, result shape, unresolved symbols, route reason, and elapsed time. Return the legacy response.

Compile-only shadowing must still fail the differential test harness when the emitted plan shape is unsupported; it must not turn an unsupported plan into an empty result.

- [ ] **Step 3: Implement sampled execution shadowing**

For selected requests, execute both engines against the same database snapshot and compare:

- resource identity and first-seen order;
- duplicate elimination;
- include match/partial classification;
- totals and count-only values;
- continuation page sequences;
- FHIR errors and OperationOutcome issues;
- history/deleted/access-control visibility.

Return the legacy response and emit a structured mismatch with query correlation ID, engine identities, parameter names (not sensitive values), and a minimal result diff.

- [ ] **Step 4: Add differential fixtures**

Cover STU3, R4, R4B, R5, tenant-specific definitions, override URLs, ordinary search, chains, composites, include/revinclude/iterate, `_summary=count`, `_total`, missing sort, history, soft delete, access control, feed ranges, and continuation pages.

Create one test helper that runs both engines from the same `SearchOptions` input and one assertion helper that compares `SearchResult` without comparing resource JSON serialization order.

- [ ] **Step 5: Run the differential suite**

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.SqlServer.UnitTests\Microsoft.Health.Fhir.SqlServer.UnitTests.csproj --configuration Debug --filter FullyQualifiedName~IgnixaDifferentialSearchTests
```

Expected: zero deterministic mismatches. Any mismatch blocks the next rollout gate and is classified by parser, symbol resolution, SQL plan, result hydration, continuation, schema, or write-path cause.

- [ ] **Step 6: Commit shadow validation**

```powershell
git add src\Microsoft.Health.Fhir.SqlServer\Features\Search src\Microsoft.Health.Fhir.SqlServer.UnitTests\Features\Search\Ignixa
git commit -m "feat: add Ignixa SQL differential shadowing"
```

## Task 11: Enable shape-scoped compiled canaries and operational rollback

**Files:**
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Ignixa/IgnixaSqlCapabilityRouter.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/SqlServerSearchService.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Configs/CoreFeatureConfiguration.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Logging/Metrics/Handlers/DefaultSearchMetricHandler.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer/Registration/FhirSqlServerConfiguration.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/Ignixa/IgnixaDifferentialSearchTests.cs`
- Modify: `src/Microsoft.Health.Fhir.SqlServer.UnitTests/Features/Search/SqlServerSearchServiceTests.cs`

- [ ] **Step 1: Define the first supported compiled shapes**

Enable compiled execution only for shapes with complete parity evidence:

```text
single resource type,
ordinary indexed predicates,
supported sort with resource-key tie-breaker,
latest non-deleted resources,
no unsupported access-control or feed-range mode,
no legacy continuation token,
no unsupported include phase.
```

All other requests route to legacy with a structured reason.

- [ ] **Step 2: Add canary routing**

Add tenant, environment, resource-type, and shape allowlists. Rollback is a routing change that sets the allowlist empty or disables `Search.Ignixa.SqlCompiler.Enabled`; it must not invalidate existing tokens or reinterpret an `IgnixaSql` token as legacy.

- [ ] **Step 3: Verify canary metrics**

Record compiled success/failure/fallback counts, result mismatches, SQL duration, CPU, logical reads, memory grants, spills, hydration duration, and continuation failures. Preserve the existing long-running query logging and retry metrics.

- [ ] **Step 4: Exercise rollback**

Add an integration test that:

1. receives a compiled continuation token;
2. disables compiled routing;
3. confirms the token remains routable only to the compiled path;
4. confirms the request returns an explicit engine-disabled/continuation error instead of a legacy page.

- [ ] **Step 5: Commit the canary gate**

```powershell
git add src\Microsoft.Health.Fhir.SqlServer\Features\Search src\Microsoft.Health.Fhir.SqlServer.UnitTests\Features\Search
git commit -m "feat: add shape-scoped Ignixa SQL canary routing"
```

## Task 12: Retire the legacy parser bridge and SQL renderer only after parity

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/SearchOptionsFactory.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/SearchModule.cs`
- Modify: `src/Microsoft.Health.Fhir.CosmosDb/Features/Search/FhirCosmosSearchService.cs`
- Delete only after the retirement criteria pass: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/ExpressionParser.cs`, `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/IExpressionParser.cs`, `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/ISearchParameterExpressionParser.cs`, `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/SearchParameterExpressionParser.cs`, and `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/SearchValueExpressionBuilderHelper.cs`.
- Delete only after the retirement criteria pass: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Expressions/Visitors/` legacy SQL rewriters.
- Delete only after the retirement criteria pass: `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Expressions/Visitors/QueryGenerators/SqlQueryGenerator.cs` and its factory types.
- Delete only after the retirement criteria pass: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/Ignixa/IgnixaLegacyExpressionBridge.cs`, `IgnixaLegacyExpressionBridgeVisitor.cs`, and `IgnixaSearchValueBridge.cs`.
- Modify: all affected unit tests to remove oracle-only coverage while retaining behavior coverage.

- [ ] **Step 1: Verify the retirement checklist**

Retirement requires:

- all enabled FHIR versions use Ignixa parsing;
- Cosmos uses native Ignixa expressions or the bridge has zero production routes;
- compiled SQL covers all enabled query shapes;
- schema, catalog, lookup tables, writes, and reindex are aligned;
- differential tests report zero deterministic mismatches;
- continuation page sequences match for primary and include searches;
- no unexplained fallback remains;
- compiled token maximum lifetime has elapsed;
- rollback routing has been exercised in the deployed environment.

- [ ] **Step 2: Remove the legacy production routes**

Remove the old parser from DI, remove the legacy expression assignment from `SearchOptions`, and make the Ignixa options property required for all production searches. Remove the Cosmos bridge only when `ExpressionQueryBuilder` has been migrated to consume Ignixa expressions directly.

- [ ] **Step 3: Remove duplicate SQL generation**

Delete the old SQL rewriters and generators as one change. Remove their registrations and tests only after the compiled tests have taken over the same behavior cases. Do not leave a second SQL renderer reachable through a fallback path for shapes declared supported.

- [ ] **Step 4: Run the full targeted validation**

Run:

```powershell
dotnet test .\src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Microsoft.Health.Fhir.Shared.Core.UnitTests.csproj --configuration Release
dotnet test .\src\Microsoft.Health.Fhir.CosmosDb.UnitTests\Microsoft.Health.Fhir.CosmosDb.UnitTests.csproj --configuration Release
dotnet test .\src\Microsoft.Health.Fhir.SqlServer.UnitTests\Microsoft.Health.Fhir.SqlServer.UnitTests.csproj --configuration Release
dotnet build .\Microsoft.Health.Fhir.sln --configuration Release --no-restore --no-incremental
```

Expected: all targeted projects pass, the solution builds, and no production project references the removed parser or SQL generator types.

- [ ] **Step 5: Commit retirement**

```powershell
git add src
git commit -m "refactor: retire legacy search parser and SQL renderer"
```

## Self-review checklist

- [ ] **Spec coverage:** Tasks 1-2 cover dependency pinning, canonical parser, FHIR versions, tenant definitions, server-only controls, and structured parser errors.
- [ ] **Spec coverage:** Task 3 covers the one-way Ignixa lowering boundary and Cosmos structural/value preservation.
- [ ] **Spec coverage:** Tasks 4-6 cover symbol resolution, request-scoped caching, URL overrides, schema/catalog identity, compiler stage order, typed parameters, result shape, and capability failures.
- [ ] **Spec coverage:** Tasks 7-9 cover SQL retry/routing, result hydration, ordering, totals, count-only, history/deleted/access-control/feed-range filters, continuation, includes, schema, writes, lookup maintenance, and reindex.
- [ ] **Spec coverage:** Tasks 10-11 cover compile-only shadowing, sampled differential execution, metrics, canaries, fallback, and rollback.
- [ ] **Spec coverage:** Task 12 covers retirement criteria and removal of duplicate parser/SQL generation only after token and parity gates.
- [ ] **Placeholder scan:** The plan contains no unspecified task placeholders or “add tests” steps; package versions are intentionally resolved from the exact upstream published artifact before restore.
- [ ] **Type consistency:** `IgnixaOptions`, `IIgnixaSearchOptionsAdapter`, `IgnixaSqlSymbolResolver`, `IgnixaSqlCompilerAdapter`, `IgnixaCompiledSearchResult`, `IgnixaCompiledContinuation`, and `SearchEngineRoute` are introduced before their consumers.
- [ ] **Behavior safety:** Compiled execution is disabled by default, legacy tokens remain legacy-only, compiled tokens remain compiled-only, and unsupported physical modes route explicitly rather than being silently approximated.
