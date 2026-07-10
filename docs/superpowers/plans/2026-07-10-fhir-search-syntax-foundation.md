# FHIR Search Syntax Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce an immutable, backend-neutral representation of the raw FHIR search request and attach it to every `SearchOptions` instance without changing existing search semantics or execution.

**Architecture:** `ISearchQuerySyntaxParser` captures the raw request into `SearchQuerySyntax` before `SearchOptionsFactory` performs any legacy interpretation. `SearchOptionsFactory` continues to build the existing `Expression`, sorting, includes, authorization constraints, and continuation state exactly as it does today; the new syntax object is carried beside that legacy output as the first strangler seam. Later plans will bind the syntax into `BoundSearchQuery`, but this plan deliberately performs no semantic validation or backend routing.

**Tech Stack:** C# 13, .NET 9, xUnit, Microsoft.Extensions.DependencyInjection, existing shared-project (`.projitems`) layout

---

## Initiative decomposition

The approved design is too broad for one safe implementation plan. Implement it as these independently reviewable plans:

| Plan | Shippable outcome |
|---|---|
| 1. Search syntax foundation (this document) | Every search carries immutable raw syntax while all legacy behavior remains primary. |
| 2. Semantic binder and legacy adapter | Raw syntax binds to a typed `BoundSearchQuery`; an adapter reproduces the current `Expression` and `SearchOptions` output. |
| 3. Logical relational plan and normalization | SQL shadow compilation lowers bound queries to typed logical operators and deterministic normalization rules. |
| 4. SQL catalog and canonical physical planner | Existing SQL schema capabilities map to a safe, deterministic physical plan for every supported shadow shape. |
| 5. Memo optimizer, costing, and plan cache | Bounded cost-based alternatives, statistics, plan families, stable shape hashing, and explain output are added. |
| 6. Typed SQL AST and differential execution | Physical plans render deterministic SQL and run in sampled shadow mode against the legacy engine. |
| 7. Shape-family canary and legacy retirement | Versioned continuations, shape-scoped rollout, rollback, and removal of promoted legacy paths are completed. |

Create each later plan only after its predecessor is merged. That preserves exact interfaces and prevents speculative file paths or signatures.

## Scope and invariants

This plan adds one representation and one production seam. It must preserve these invariants:

1. `SearchOptions.Expression`, sort, includes, totals, unsupported parameters, query hints, authorization constraints, and continuations remain produced by the existing code path.
2. Duplicate raw parameters remain present and ordered in `SearchQuerySyntax`, even where the legacy expression path intentionally deduplicates identical predicates.
3. Null query-parameter input becomes an empty syntax parameter list.
4. The syntax parser performs no FHIR validation, SearchParameter lookup, decoding, deduplication, normalization, or backend-specific interpretation.
5. `SqlSearchOptions(SearchOptions)` preserves the same syntax object through the existing copy constructor.
6. The change is active for every request and needs no feature flag because it is passive data capture with no execution effect.

## File structure

### New production files

- `src/Microsoft.Health.Fhir.Core/Features/Search/Syntax/SearchQueryParameterSyntax.cs` — immutable raw name, value, and ordinal.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Syntax/SearchQuerySyntax.cs` — immutable request envelope containing resource, compartment, flags, and ordered parameters.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Syntax/ISearchQuerySyntaxParser.cs` — single parsing seam used by `SearchOptionsFactory`.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Syntax/SearchQuerySyntaxParser.cs` — lossless raw request capture only.

### New test files

- `src/Microsoft.Health.Fhir.Core.UnitTests/Features/Search/Syntax/SearchQuerySyntaxTests.cs` — immutability and request-envelope tests.
- `src/Microsoft.Health.Fhir.Core.UnitTests/Features/Search/Syntax/SearchQuerySyntaxParserTests.cs` — null, order, duplicates, and metadata preservation.
- `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/SearchModuleTests.cs` — DI lifetime and implementation registration.

### Modified files

- `src/Microsoft.Health.Fhir.Core/Features/Search/SearchOptions.cs` — carries `QuerySyntax` through the legacy pipeline and copy constructor.
- `src/Microsoft.Health.Fhir.Core.UnitTests/Features/Search/SearchOptionsTests.cs` — proves copy behavior.
- `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/SearchOptionsFactory.cs` — captures syntax before legacy interpretation.
- `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/SearchOptionsFactoryTests.cs` — proves syntax capture and updates direct constructor calls.
- `src/Microsoft.Health.Fhir.Shared.Api/Modules/SearchModule.cs` — registers the syntax parser as a singleton.
- `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems` — includes the new shared API test.
- `docs/SearchArchitecture.md` — documents the passive syntax seam and explicitly states that legacy execution is unchanged.

## Task 1: Add immutable search syntax value objects

**Files:**
- Create: `src/Microsoft.Health.Fhir.Core/Features/Search/Syntax/SearchQueryParameterSyntax.cs`
- Create: `src/Microsoft.Health.Fhir.Core/Features/Search/Syntax/SearchQuerySyntax.cs`
- Create: `src/Microsoft.Health.Fhir.Core.UnitTests/Features/Search/Syntax/SearchQuerySyntaxTests.cs`

- [ ] **Step 1: Write the failing immutability test**

Create `SearchQuerySyntaxTests.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Search.Syntax;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.Syntax
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class SearchQuerySyntaxTests
    {
        [Fact]
        public void GivenMutableInput_WhenSyntaxIsCreated_ThenRequestStateIsCapturedImmutably()
        {
            var parameters = new List<SearchQueryParameterSyntax>
            {
                new SearchQueryParameterSyntax("name", "Smith", 0),
            };

            var syntax = new SearchQuerySyntax(
                compartmentType: "Patient",
                compartmentId: "123",
                resourceType: "Observation",
                parameters: parameters,
                isAsyncOperation: true,
                useSmartCompartmentDefinition: true,
                resourceVersionTypes: ResourceVersionType.Latest | ResourceVersionType.History,
                onlyIds: true,
                isIncludesOperation: true);

            parameters.Clear();

            SearchQueryParameterSyntax parameter = Assert.Single(syntax.Parameters);
            Assert.Equal("name", parameter.Name);
            Assert.Equal("Smith", parameter.Value);
            Assert.Equal(0, parameter.Ordinal);
            Assert.Equal("Patient", syntax.CompartmentType);
            Assert.Equal("123", syntax.CompartmentId);
            Assert.Equal("Observation", syntax.ResourceType);
            Assert.True(syntax.IsAsyncOperation);
            Assert.True(syntax.UseSmartCompartmentDefinition);
            Assert.Equal(ResourceVersionType.Latest | ResourceVersionType.History, syntax.ResourceVersionTypes);
            Assert.True(syntax.OnlyIds);
            Assert.True(syntax.IsIncludesOperation);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchQuerySyntaxTests" --no-restore --verbosity quiet
```

Expected: FAIL to compile because `Microsoft.Health.Fhir.Core.Features.Search.Syntax` and its two types do not exist.

- [ ] **Step 3: Implement the raw parameter value object**

Create `SearchQueryParameterSyntax.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Search.Syntax
{
    /// <summary>
    /// Represents one unbound query parameter exactly as it appeared in a search request.
    /// </summary>
    public sealed class SearchQueryParameterSyntax
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SearchQueryParameterSyntax"/> class.
        /// </summary>
        /// <param name="name">The raw parameter name.</param>
        /// <param name="value">The raw parameter value.</param>
        /// <param name="ordinal">The zero-based parameter position in the request.</param>
        public SearchQueryParameterSyntax(string name, string value, int ordinal)
        {
            Name = name;
            Value = value;
            Ordinal = ordinal;
        }

        /// <summary>
        /// Gets the raw parameter name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the raw parameter value.
        /// </summary>
        public string Value { get; }

        /// <summary>
        /// Gets the zero-based parameter position in the request.
        /// </summary>
        public int Ordinal { get; }
    }
}
```

- [ ] **Step 4: Implement the immutable request envelope**

Create `SearchQuerySyntax.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Health.Fhir.Core.Features.Search.Syntax
{
    /// <summary>
    /// Represents an unbound FHIR search request without interpreting its parameters.
    /// </summary>
    public sealed class SearchQuerySyntax
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SearchQuerySyntax"/> class.
        /// </summary>
        public SearchQuerySyntax(
            string compartmentType,
            string compartmentId,
            string resourceType,
            IReadOnlyList<SearchQueryParameterSyntax> parameters,
            bool isAsyncOperation,
            bool useSmartCompartmentDefinition,
            ResourceVersionType resourceVersionTypes,
            bool onlyIds,
            bool isIncludesOperation)
        {
            CompartmentType = compartmentType;
            CompartmentId = compartmentId;
            ResourceType = resourceType;
            Parameters = Array.AsReadOnly(parameters?.ToArray() ?? Array.Empty<SearchQueryParameterSyntax>());
            IsAsyncOperation = isAsyncOperation;
            UseSmartCompartmentDefinition = useSmartCompartmentDefinition;
            ResourceVersionTypes = resourceVersionTypes;
            OnlyIds = onlyIds;
            IsIncludesOperation = isIncludesOperation;
        }

        /// <summary>
        /// Gets the raw compartment type.
        /// </summary>
        public string CompartmentType { get; }

        /// <summary>
        /// Gets the raw compartment identifier.
        /// </summary>
        public string CompartmentId { get; }

        /// <summary>
        /// Gets the raw resource type.
        /// </summary>
        public string ResourceType { get; }

        /// <summary>
        /// Gets the ordered raw query parameters.
        /// </summary>
        public IReadOnlyList<SearchQueryParameterSyntax> Parameters { get; }

        /// <summary>
        /// Gets a value indicating whether the request is an asynchronous operation.
        /// </summary>
        public bool IsAsyncOperation { get; }

        /// <summary>
        /// Gets a value indicating whether SMART compartment definitions apply.
        /// </summary>
        public bool UseSmartCompartmentDefinition { get; }

        /// <summary>
        /// Gets the requested resource-version kinds.
        /// </summary>
        public ResourceVersionType ResourceVersionTypes { get; }

        /// <summary>
        /// Gets a value indicating whether only resource identifiers are requested.
        /// </summary>
        public bool OnlyIds { get; }

        /// <summary>
        /// Gets a value indicating whether this is an includes operation.
        /// </summary>
        public bool IsIncludesOperation { get; }
    }
}
```

- [ ] **Step 5: Run the focused test**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchQuerySyntaxTests" --no-restore --verbosity quiet
```

Expected: PASS with one test.

- [ ] **Step 6: Commit the syntax value objects**

```powershell
git add "src\Microsoft.Health.Fhir.Core\Features\Search\Syntax\SearchQueryParameterSyntax.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Syntax\SearchQuerySyntax.cs" "src\Microsoft.Health.Fhir.Core.UnitTests\Features\Search\Syntax\SearchQuerySyntaxTests.cs"
git commit -m "Add immutable FHIR search syntax model" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Task 2: Add lossless raw syntax parsing

**Files:**
- Create: `src/Microsoft.Health.Fhir.Core/Features/Search/Syntax/ISearchQuerySyntaxParser.cs`
- Create: `src/Microsoft.Health.Fhir.Core/Features/Search/Syntax/SearchQuerySyntaxParser.cs`
- Create: `src/Microsoft.Health.Fhir.Core.UnitTests/Features/Search/Syntax/SearchQuerySyntaxParserTests.cs`

- [ ] **Step 1: Write parser tests for null input, order, duplicates, and metadata**

Create `SearchQuerySyntaxParserTests.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Search.Syntax;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.Syntax
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class SearchQuerySyntaxParserTests
    {
        private readonly SearchQuerySyntaxParser _parser = new SearchQuerySyntaxParser();

        [Fact]
        public void GivenNullParameters_WhenParsed_ThenSyntaxContainsAnEmptyParameterList()
        {
            SearchQuerySyntax syntax = _parser.Parse(
                compartmentType: null,
                compartmentId: null,
                resourceType: "Patient",
                queryParameters: null,
                isAsyncOperation: false,
                useSmartCompartmentDefinition: false,
                resourceVersionTypes: ResourceVersionType.Latest,
                onlyIds: false,
                isIncludesOperation: false);

            Assert.Empty(syntax.Parameters);
            Assert.Equal("Patient", syntax.ResourceType);
        }

        [Fact]
        public void GivenDuplicateOrderedParameters_WhenParsed_ThenEveryRawOccurrenceIsPreserved()
        {
            var queryParameters = new[]
            {
                Tuple.Create("_tag", "system|code"),
                Tuple.Create("_sort", "-date"),
                Tuple.Create("_tag", "system|code"),
            };

            SearchQuerySyntax syntax = _parser.Parse(
                compartmentType: "Patient",
                compartmentId: "123",
                resourceType: "Observation",
                queryParameters: queryParameters,
                isAsyncOperation: true,
                useSmartCompartmentDefinition: true,
                resourceVersionTypes: ResourceVersionType.History,
                onlyIds: true,
                isIncludesOperation: true);

            Assert.Collection(
                syntax.Parameters,
                parameter =>
                {
                    Assert.Equal("_tag", parameter.Name);
                    Assert.Equal("system|code", parameter.Value);
                    Assert.Equal(0, parameter.Ordinal);
                },
                parameter =>
                {
                    Assert.Equal("_sort", parameter.Name);
                    Assert.Equal("-date", parameter.Value);
                    Assert.Equal(1, parameter.Ordinal);
                },
                parameter =>
                {
                    Assert.Equal("_tag", parameter.Name);
                    Assert.Equal("system|code", parameter.Value);
                    Assert.Equal(2, parameter.Ordinal);
                });
            Assert.Equal("Patient", syntax.CompartmentType);
            Assert.Equal("123", syntax.CompartmentId);
            Assert.Equal("Observation", syntax.ResourceType);
            Assert.True(syntax.IsAsyncOperation);
            Assert.True(syntax.UseSmartCompartmentDefinition);
            Assert.Equal(ResourceVersionType.History, syntax.ResourceVersionTypes);
            Assert.True(syntax.OnlyIds);
            Assert.True(syntax.IsIncludesOperation);
        }
    }
}
```

- [ ] **Step 2: Run the parser tests to verify they fail**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchQuerySyntaxParserTests" --no-restore --verbosity quiet
```

Expected: FAIL to compile because `SearchQuerySyntaxParser` does not exist.

- [ ] **Step 3: Add the parser contract**

Create `ISearchQuerySyntaxParser.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Core.Features.Search.Syntax
{
    /// <summary>
    /// Captures a raw FHIR search request as backend-neutral syntax.
    /// </summary>
    public interface ISearchQuerySyntaxParser
    {
        /// <summary>
        /// Captures the request without resolving or validating search semantics.
        /// </summary>
        SearchQuerySyntax Parse(
            string compartmentType,
            string compartmentId,
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            bool isAsyncOperation,
            bool useSmartCompartmentDefinition,
            ResourceVersionType resourceVersionTypes,
            bool onlyIds,
            bool isIncludesOperation);
    }
}
```

- [ ] **Step 4: Implement lossless capture**

Create `SearchQuerySyntaxParser.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Health.Fhir.Core.Features.Search.Syntax
{
    /// <summary>
    /// Captures raw FHIR search request syntax without semantic interpretation.
    /// </summary>
    public sealed class SearchQuerySyntaxParser : ISearchQuerySyntaxParser
    {
        /// <inheritdoc />
        public SearchQuerySyntax Parse(
            string compartmentType,
            string compartmentId,
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            bool isAsyncOperation,
            bool useSmartCompartmentDefinition,
            ResourceVersionType resourceVersionTypes,
            bool onlyIds,
            bool isIncludesOperation)
        {
            IReadOnlyList<SearchQueryParameterSyntax> parameters = queryParameters?
                .Select((parameter, ordinal) => new SearchQueryParameterSyntax(parameter.Item1, parameter.Item2, ordinal))
                .ToArray()
                ?? Array.Empty<SearchQueryParameterSyntax>();

            return new SearchQuerySyntax(
                compartmentType,
                compartmentId,
                resourceType,
                parameters,
                isAsyncOperation,
                useSmartCompartmentDefinition,
                resourceVersionTypes,
                onlyIds,
                isIncludesOperation);
        }
    }
}
```

- [ ] **Step 5: Run all syntax tests**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchQuerySyntax" --no-restore --verbosity quiet
```

Expected: PASS with three tests.

- [ ] **Step 6: Commit the parser**

```powershell
git add "src\Microsoft.Health.Fhir.Core\Features\Search\Syntax\ISearchQuerySyntaxParser.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Syntax\SearchQuerySyntaxParser.cs" "src\Microsoft.Health.Fhir.Core.UnitTests\Features\Search\Syntax\SearchQuerySyntaxParserTests.cs"
git commit -m "Add raw FHIR search syntax parser" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Task 3: Carry syntax through `SearchOptions`

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Search/SearchOptions.cs`
- Modify: `src/Microsoft.Health.Fhir.Core.UnitTests/Features/Search/SearchOptionsTests.cs`

- [ ] **Step 1: Write a failing copy-constructor test**

Add this import to `SearchOptionsTests.cs`:

```csharp
using Microsoft.Health.Fhir.Core.Features.Search.Syntax;
```

Add this test to `SearchOptionsTests`:

```csharp
[Fact]
public void GivenQuerySyntax_WhenSearchOptionsIsCopied_ThenTheImmutableSyntaxIsPreserved()
{
    var syntax = new SearchQuerySyntax(
        compartmentType: null,
        compartmentId: null,
        resourceType: "Patient",
        parameters: null,
        isAsyncOperation: false,
        useSmartCompartmentDefinition: false,
        resourceVersionTypes: ResourceVersionType.Latest,
        onlyIds: false,
        isIncludesOperation: false);
    var original = new SearchOptions
    {
        QuerySyntax = syntax,
    };

    var copy = new SearchOptions(original);

    Assert.Same(syntax, copy.QuerySyntax);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~GivenQuerySyntax_WhenSearchOptionsIsCopied" --no-restore --verbosity quiet
```

Expected: FAIL to compile because `SearchOptions.QuerySyntax` does not exist.

- [ ] **Step 3: Add the syntax property and copy behavior**

Add this import to `SearchOptions.cs`:

```csharp
using Microsoft.Health.Fhir.Core.Features.Search.Syntax;
```

In `SearchOptions(SearchOptions other)`, copy the syntax beside `Expression`:

```csharp
Expression = other.Expression;
QuerySyntax = other.QuerySyntax;
```

Add this property immediately before `Expression`:

```csharp
/// <summary>
/// Gets the unbound syntax captured from the original search request.
/// </summary>
public SearchQuerySyntax QuerySyntax { get; internal set; }
```

- [ ] **Step 4: Run the focused tests**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsTests" --no-restore --verbosity quiet
```

Expected: PASS, including the new copy-constructor test.

- [ ] **Step 5: Commit `SearchOptions` propagation**

```powershell
git add "src\Microsoft.Health.Fhir.Core\Features\Search\SearchOptions.cs" "src\Microsoft.Health.Fhir.Core.UnitTests\Features\Search\SearchOptionsTests.cs"
git commit -m "Carry raw syntax through search options" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Task 4: Capture syntax in `SearchOptionsFactory`

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/SearchOptionsFactory.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/SearchOptionsFactoryTests.cs`

- [ ] **Step 1: Update direct factory construction and write the failing integration test**

Add this import to `SearchOptionsFactoryTests.cs`:

```csharp
using Microsoft.Health.Fhir.Core.Features.Search.Syntax;
```

Add a field:

```csharp
private readonly ISearchQuerySyntaxParser _searchQuerySyntaxParser = new SearchQuerySyntaxParser();
```

Pass `_searchQuerySyntaxParser` immediately after `_expressionParser` in the constructor call at the top of the test class:

```csharp
_factory = new SearchOptionsFactory(
    _expressionParser,
    _searchQuerySyntaxParser,
    () => searchParameterDefinitionManager,
    new OptionsWrapper<CoreFeatureConfiguration>(_coreFeatures),
    contextAccessor,
    _sortingValidator,
    new ExpressionAccessControl(contextAccessor),
    NullLogger<SearchOptionsFactory>.Instance);
```

Pass `new SearchQuerySyntaxParser()` immediately after `stubExpressionParser` in the factory construction inside `Create_AddsFineGrainedAccessControlWithSearchParametersExpressions_UsingMemberData`.

Add this test:

```csharp
[Fact]
public void GivenRawRequestMetadata_WhenCreated_ThenLosslessSyntaxIsAttachedBeforeLegacyInterpretation()
{
    var queryParameters = new[]
    {
        Tuple.Create("_tag", "system|code"),
        Tuple.Create("_sort", "-_lastUpdated"),
        Tuple.Create("_tag", "system|code"),
    };

    SearchOptions options = _factory.Create(
        compartmentType: "Patient",
        compartmentId: "123",
        resourceType: DefaultResourceType,
        queryParameters: queryParameters,
        isAsyncOperation: true,
        useSmartCompartmentDefinition: true,
        resourceVersionTypes: ResourceVersionType.History,
        onlyIds: true,
        isIncludesOperation: false);

    Assert.Equal("Patient", options.QuerySyntax.CompartmentType);
    Assert.Equal("123", options.QuerySyntax.CompartmentId);
    Assert.Equal(DefaultResourceType, options.QuerySyntax.ResourceType);
    Assert.True(options.QuerySyntax.IsAsyncOperation);
    Assert.True(options.QuerySyntax.UseSmartCompartmentDefinition);
    Assert.Equal(ResourceVersionType.History, options.QuerySyntax.ResourceVersionTypes);
    Assert.True(options.QuerySyntax.OnlyIds);
    Assert.False(options.QuerySyntax.IsIncludesOperation);
    Assert.Collection(
        options.QuerySyntax.Parameters,
        parameter => Assert.Equal(("_tag", "system|code", 0), (parameter.Name, parameter.Value, parameter.Ordinal)),
        parameter => Assert.Equal(("_sort", "-_lastUpdated", 1), (parameter.Name, parameter.Value, parameter.Ordinal)),
        parameter => Assert.Equal(("_tag", "system|code", 2), (parameter.Name, parameter.Value, parameter.Ordinal)));
}
```

- [ ] **Step 2: Run the shared R4 factory tests to verify failure**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-restore --verbosity quiet
```

Expected: FAIL to compile because `SearchOptionsFactory` does not accept `ISearchQuerySyntaxParser`.

- [ ] **Step 3: Inject and validate the syntax parser**

Add this import to `SearchOptionsFactory.cs`:

```csharp
using Microsoft.Health.Fhir.Core.Features.Search.Syntax;
```

Add this field beside `_expressionParser`:

```csharp
private readonly ISearchQuerySyntaxParser _searchQuerySyntaxParser;
```

Update the constructor:

```csharp
public SearchOptionsFactory(
    IExpressionParser expressionParser,
    ISearchQuerySyntaxParser searchQuerySyntaxParser,
    ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver searchParameterDefinitionManagerResolver,
    IOptions<CoreFeatureConfiguration> featureConfiguration,
    RequestContextAccessor<IFhirRequestContext> contextAccessor,
    ISortingValidator sortingValidator,
    ExpressionAccessControl expressionAccess,
    ILogger<SearchOptionsFactory> logger)
{
    EnsureArg.IsNotNull(expressionParser, nameof(expressionParser));
    EnsureArg.IsNotNull(searchQuerySyntaxParser, nameof(searchQuerySyntaxParser));
    EnsureArg.IsNotNull(searchParameterDefinitionManagerResolver, nameof(searchParameterDefinitionManagerResolver));
    EnsureArg.IsNotNull(featureConfiguration?.Value, nameof(featureConfiguration));
    EnsureArg.IsNotNull(contextAccessor, nameof(contextAccessor));
    EnsureArg.IsNotNull(sortingValidator, nameof(sortingValidator));
    EnsureArg.IsNotNull(expressionAccess, nameof(expressionAccess));
    EnsureArg.IsNotNull(logger, nameof(logger));

    _expressionParser = expressionParser;
    _searchQuerySyntaxParser = searchQuerySyntaxParser;
    _contextAccessor = contextAccessor;
    _sortingValidator = sortingValidator;
    _expressionAccess = expressionAccess;
    _searchParameterDefinitionManager = searchParameterDefinitionManagerResolver();
    _logger = logger;
    _featureConfiguration = featureConfiguration.Value;
}
```

- [ ] **Step 4: Attach syntax before existing interpretation**

Replace the first line of the long `Create` overload:

```csharp
var searchOptions = new SearchOptions();
```

with:

```csharp
SearchQuerySyntax querySyntax = _searchQuerySyntaxParser.Parse(
    compartmentType,
    compartmentId,
    resourceType,
    queryParameters,
    isAsyncOperation,
    useSmartCompartmentDefinition,
    resourceVersionTypes,
    onlyIds,
    isIncludesOperation);

var searchOptions = new SearchOptions
{
    QuerySyntax = querySyntax,
};
```

Do not change any subsequent interpretation, validation, deduplication, expression construction, or error behavior in this task.

- [ ] **Step 5: Run R4 factory tests**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-restore --verbosity quiet
```

Expected: PASS, including the new syntax-capture test and all existing legacy-behavior tests.

- [ ] **Step 6: Run all version-specific factory tests**

Run each command:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Stu3.Core.UnitTests\Microsoft.Health.Fhir.Stu3.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-restore --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-restore --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4B.Core.UnitTests\Microsoft.Health.Fhir.R4B.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-restore --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R5.Core.UnitTests\Microsoft.Health.Fhir.R5.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-restore --verbosity quiet
```

Expected: PASS for STU3, R4, R4B, and R5.

- [ ] **Step 7: Commit factory integration**

```powershell
git add "src\Microsoft.Health.Fhir.Shared.Core\Features\Search\SearchOptionsFactory.cs" "src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\SearchOptionsFactoryTests.cs"
git commit -m "Capture search syntax before legacy parsing" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Task 5: Register and verify the syntax parser

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/SearchModule.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/SearchModuleTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems`

- [ ] **Step 1: Write a failing DI registration test**

Create `SearchModuleTests.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Api.Modules;
using Microsoft.Health.Fhir.Core.Features.Search.Syntax;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Modules
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class SearchModuleTests
    {
        [Fact]
        public void GivenSearchModule_WhenLoaded_ThenSyntaxParserIsRegisteredAsSingleton()
        {
            var services = new ServiceCollection();
            var module = new SearchModule(new FhirServerConfiguration());

            module.Load(services);

            ServiceDescriptor descriptor = Assert.Single(
                services.Where(service => service.ServiceType == typeof(ISearchQuerySyntaxParser)));
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(typeof(SearchQuerySyntaxParser), descriptor.ImplementationType);
        }
    }
}
```

Add this compile item near the other module-level tests in `Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems`:

```xml
<Compile Include="$(MSBuildThisFileDirectory)Modules\SearchModuleTests.cs" />
```

- [ ] **Step 2: Run the registration test to verify it fails**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Api.UnitTests\Microsoft.Health.Fhir.R4.Api.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchModuleTests" --no-restore --verbosity quiet
```

Expected: FAIL because no `ISearchQuerySyntaxParser` descriptor is registered.

- [ ] **Step 3: Register the parser**

Add the syntax namespace to `SearchModule.cs`:

```csharp
using Microsoft.Health.Fhir.Core.Features.Search.Syntax;
```

Register the parser immediately before the existing expression parsers:

```csharp
services.AddSingleton<ISearchQuerySyntaxParser, SearchQuerySyntaxParser>();
services.AddSingleton<ISearchParameterExpressionParser, SearchParameterExpressionParser>();
services.AddSingleton<IExpressionParser, ExpressionParser>();
services.AddSingleton<ISearchOptionsFactory, SearchOptionsFactory>();
```

- [ ] **Step 4: Run the registration test**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Api.UnitTests\Microsoft.Health.Fhir.R4.Api.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchModuleTests" --no-restore --verbosity quiet
```

Expected: PASS with one test.

- [ ] **Step 5: Commit DI registration**

```powershell
git add "src\Microsoft.Health.Fhir.Shared.Api\Modules\SearchModule.cs" "src\Microsoft.Health.Fhir.Shared.Api.UnitTests\Modules\SearchModuleTests.cs" "src\Microsoft.Health.Fhir.Shared.Api.UnitTests\Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems"
git commit -m "Register FHIR search syntax parser" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Task 6: Document the transition seam and run final verification

**Files:**
- Modify: `docs/SearchArchitecture.md`

- [ ] **Step 1: Document the passive syntax stage**

Insert this section before `## Extraction`:

```markdown
## Request compilation

Search requests are captured first as an immutable `SearchQuerySyntax`. This representation preserves the raw resource and compartment context, request flags, parameter values, duplicates, and parameter order without resolving SearchParameters or making storage decisions.

`SearchOptionsFactory` currently carries this syntax beside the existing `Expression` output. The existing expression parser and SQL/Cosmos execution paths remain authoritative during the strangler migration. The next compiler stage will bind `SearchQuerySyntax` into backend-neutral FHIR semantics before adapting that bound query back to the legacy expression model.
```

- [ ] **Step 2: Run formatting and diff checks**

Run:

```powershell
dotnet format "Microsoft.Health.Fhir.sln" --no-restore --include "src\Microsoft.Health.Fhir.Core\Features\Search\Syntax" "src\Microsoft.Health.Fhir.Core\Features\Search\SearchOptions.cs" "src\Microsoft.Health.Fhir.Core.UnitTests\Features\Search\Syntax" "src\Microsoft.Health.Fhir.Core.UnitTests\Features\Search\SearchOptionsTests.cs" "src\Microsoft.Health.Fhir.Shared.Core\Features\Search\SearchOptionsFactory.cs" "src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\SearchOptionsFactoryTests.cs" "src\Microsoft.Health.Fhir.Shared.Api\Modules\SearchModule.cs" "src\Microsoft.Health.Fhir.Shared.Api.UnitTests\Modules\SearchModuleTests.cs"
git --no-pager diff --check
```

Expected: both commands exit with code 0 and `git diff --check` prints no errors.

- [ ] **Step 3: Build the solution for the primary framework**

Run:

```powershell
dotnet build "Microsoft.Health.Fhir.sln" --configuration Debug --framework net9.0 --no-restore -warnaserror
```

Expected: build succeeds with 0 warnings and 0 errors.

- [ ] **Step 4: Run focused Core and API tests**

Run:

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchQuerySyntax|FullyQualifiedName~SearchOptionsTests" --no-build --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.Stu3.Core.UnitTests\Microsoft.Health.Fhir.Stu3.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-build --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-build --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4B.Core.UnitTests\Microsoft.Health.Fhir.R4B.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-build --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R5.Core.UnitTests\Microsoft.Health.Fhir.R5.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchOptionsFactoryTests" --no-build --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.Api.UnitTests\Microsoft.Health.Fhir.R4.Api.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchModuleTests" --no-build --verbosity quiet
```

Expected: every command passes with zero failed tests.

- [ ] **Step 5: Verify the production boundary**

Run:

```powershell
git --no-pager diff -- "src\Microsoft.Health.Fhir.SqlServer" "src\Microsoft.Health.Fhir.CosmosDb"
```

Expected: no output. Plan 1 must not change either backend or route execution through the new syntax.

- [ ] **Step 6: Commit documentation**

```powershell
git add "docs\SearchArchitecture.md"
git commit -m "Document FHIR search syntax compilation seam" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Completion criteria

Plan 1 is complete only when:

- every `SearchOptionsFactory.Create` call attaches a non-null `SearchQuerySyntax`;
- raw query order and duplicates are preserved;
- `SearchOptions` and `SqlSearchOptions` copies retain the same immutable syntax object;
- all pre-existing `SearchOptionsFactoryTests` pass for STU3, R4, R4B, and R5;
- DI resolves the parser as a singleton;
- no SQL Server or Cosmos code changes;
- no search behavior, errors, warnings, or generated expressions change;
- the solution builds for `net9.0` with warnings treated as errors.
