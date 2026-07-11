# FHIR Search Semantic Expression Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Evolve the existing Core `Expression` hierarchy with a semantic search-predicate leaf that preserves FHIR comparator, modifier, normalized value, and SearchParameter identity before one compatibility visitor lowers it to today's field-level expressions.

**Architecture:** `SearchParameterExpressionParser.ParseSemantic` creates semantic expressions using the existing `Expression` structural nodes. `LegacyExpressionLowerer` is the only component that translates those semantic predicates into `FieldName`, `BinaryExpression`, and `StringExpression` shapes. The existing `Parse` API immediately lowers semantic output, so SQL, Cosmos, member match, and all current callers remain unchanged in this increment.

**Tech Stack:** C# 13, .NET 9, xUnit, existing expression visitor infrastructure

---

## Initiative decomposition

Implement the north-star as independently reviewable plans:

| Plan | Shippable outcome |
|---|---|
| 1. Semantic expression foundation (this document) | Search-parameter values first become semantic expression leaves; one lowerer reproduces legacy field expressions. |
| 2. Full semantic query retention | `ExpressionParser` builds complete semantic trees; `SearchOptions` temporarily carries semantic and lowered trees. |
| 3. Logical relational plan and normalization | SQL shadow compilation lowers semantic expressions to typed relational operators. |
| 4. SQL catalog and canonical physical planner | Existing SQL schema capabilities map to safe deterministic physical plans. |
| 5. Memo optimizer, costing, and plan cache | Bounded alternatives, statistics, plan families, shape hashing, and explain output. |
| 6. Typed SQL AST and differential execution | Physical plans render deterministic SQL and execute in sampled shadow mode. |
| 7. Shape-family canary and legacy retirement | Versioned continuations, rollout, rollback, and removal of promoted legacy paths. |

Do not create a raw `SearchQuerySyntax` model. Raw query tuples are transient parser input, the semantic expression is the first durable compiler representation.

## Scope and invariants

1. Keep `IExpressionParser.Parse` and `ISearchParameterExpressionParser.Parse` behavior unchanged.
2. Reuse `SearchParameterExpression`, `MultiaryExpression`, `NotExpression`, `MissingSearchParameterExpression`, and other existing structural nodes.
3. Add only one new semantic leaf: `SearchParameterPredicateExpression`.
4. Preserve `SearchParameterInfo`, `SearchModifier`, `SearchComparator`, component index, and normalized `ISearchValue` until legacy lowering.
5. Keep `SearchValueExpressionBuilderHelper` as the authoritative legacy field-mapping implementation and call it only from `LegacyExpressionLowerer`.
6. Make accidental delivery of a semantic leaf to SQL/Cosmos explicit through the visitor contract; no backend visitor handles semantic predicates in this plan.
7. Preserve every existing parser test for STU3, R4, R4B, and R5.
8. Keep semantic-leaf `ValueInsensitiveEquals` conservative by including the normalized value. Plan 5 will replace this with canonical shape hashing that distinguishes optional-value structures without including literals.

## Baseline verification

Before editing, restore dependencies, build Core, and run the existing R4 search-value expression tests:

```powershell
dotnet restore "Microsoft.Health.Fhir.sln" --verbosity quiet
dotnet build "src\Microsoft.Health.Fhir.Core\Microsoft.Health.Fhir.Core.csproj" --framework net9.0 --no-restore -warnaserror
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
```

Expected: all commands pass. Stop and investigate any baseline failure before changing code.

## File structure

### New files

- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/SearchParameterPredicateExpression.cs` — semantic leaf.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/LegacyExpressionLowerer.cs` — semantic-to-legacy compatibility boundary.
- `src/Microsoft.Health.Fhir.Core.UnitTests/Features/Search/Expressions/LegacyExpressionLowererTests.cs` — focused lowering tests.

### Modified files

- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/IExpressionVisitor.cs` — semantic visit contract.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/DefaultExpressionVisitor.cs` — default semantic-leaf behavior.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/ExpressionRewriter.cs` — preserves semantic leaves unless overridden.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/ISearchParameterExpressionParser.cs` — exposes `ParseSemantic`.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/SearchParameterExpressionParser.cs` — constructs semantic leaves, then lowers for the existing API.
- `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/Expressions/Parsers/SearchValueExpressionBuilderTests.cs` — semantic-shape and compatibility tests.
- `docs/SearchArchitecture.md` — documents semantic construction and compatibility lowering.

## Task 1: Add the semantic search-predicate leaf

**Files:**
- Create: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/SearchParameterPredicateExpression.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/IExpressionVisitor.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/DefaultExpressionVisitor.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/ExpressionRewriter.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/Expressions/Parsers/SearchValueExpressionBuilderTests.cs`

- [ ] **Step 1: Write a failing semantic-leaf test**

Add this test to `SearchValueExpressionBuilderTests`:

```csharp
[Fact]
public void GivenDateEquality_WhenSemanticPredicateIsConstructed_ThenComparatorAndNormalizedValueArePreserved()
{
    SearchParameterInfo searchParameter = CreateSearchParameter(SearchParamType.Date);
    var value = DateTimeSearchValue.Parse("2026-07");

    var predicate = new SearchParameterPredicateExpression(
        searchParameter,
        modifier: null,
        SearchComparator.Eq,
        componentIndex: null,
        value);

    Assert.Same(searchParameter, predicate.Parameter);
    Assert.Null(predicate.Modifier);
    Assert.Equal(SearchComparator.Eq, predicate.Comparator);
    Assert.Null(predicate.ComponentIndex);
    var date = Assert.IsType<DateTimeSearchValue>(predicate.Value);
    Assert.Equal(2026, date.Start.Year);
    Assert.Equal(7, date.Start.Month);
    Assert.Equal(1, date.Start.Day);
    Assert.Equal(2026, date.End.Year);
    Assert.Equal(7, date.End.Month);
    Assert.Equal(31, date.End.Day);
}
```

- [ ] **Step 2: Run the test to verify it fails**

```powershell
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~GivenDateEquality_WhenSemanticPredicateIsConstructed" --no-restore --verbosity quiet
```

Expected: FAIL to compile because `SearchParameterPredicateExpression` does not exist.

- [ ] **Step 3: Add the semantic expression**

Create `SearchParameterPredicateExpression.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.Core.Features.Search.Expressions
{
    /// <summary>
    /// Represents a semantic FHIR search predicate before it is lowered to storage fields.
    /// </summary>
    public sealed class SearchParameterPredicateExpression : Expression
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SearchParameterPredicateExpression"/> class.
        /// </summary>
        /// <param name="parameter">The resolved search parameter.</param>
        /// <param name="modifier">The FHIR search modifier.</param>
        /// <param name="comparator">The FHIR search comparator.</param>
        /// <param name="componentIndex">The zero-based composite component position, when applicable.</param>
        /// <param name="value">The normalized search value.</param>
        public SearchParameterPredicateExpression(
            SearchParameterInfo parameter,
            SearchModifier modifier,
            SearchComparator comparator,
            int? componentIndex,
            ISearchValue value)
        {
            EnsureArg.IsNotNull(parameter, nameof(parameter));
            EnsureArg.IsNotNull(value, nameof(value));

            if (!Enum.IsDefined(comparator))
            {
                throw new ArgumentOutOfRangeException(nameof(comparator));
            }

            if (componentIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(componentIndex));
            }

            Parameter = parameter;
            Modifier = modifier;
            Comparator = comparator;
            ComponentIndex = componentIndex;
            Value = value;
        }

        /// <summary>
        /// Gets the resolved search parameter whose value is tested.
        /// </summary>
        public SearchParameterInfo Parameter { get; }

        /// <summary>
        /// Gets the FHIR search modifier.
        /// </summary>
        public SearchModifier Modifier { get; }

        /// <summary>
        /// Gets the FHIR search comparator.
        /// </summary>
        public SearchComparator Comparator { get; }

        /// <summary>
        /// Gets the composite component position, when applicable.
        /// </summary>
        public int? ComponentIndex { get; }

        /// <summary>
        /// Gets the normalized search value.
        /// </summary>
        public ISearchValue Value { get; }

        /// <inheritdoc />
        public override TOutput AcceptVisitor<TContext, TOutput>(IExpressionVisitor<TContext, TOutput> visitor, TContext context)
        {
            EnsureArg.IsNotNull(visitor, nameof(visitor));
            return visitor.VisitSearchParameterPredicate(this, context);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            string modifier = Modifier == null ? null : $":{Modifier}";
            string component = ComponentIndex.HasValue ? $"[{ComponentIndex}]." : null;
            string value = Value is TokenSearchValue { Text: not null } token ? token.Text : Value.ToString();
            return $"(SearchPredicate {component}{Parameter.Code}{modifier} {Comparator} '{value}')";
        }

        /// <inheritdoc />
        public override void AddValueInsensitiveHashCode(ref HashCode hashCode)
        {
            hashCode.Add(typeof(SearchParameterPredicateExpression));
            hashCode.Add(Parameter);
            hashCode.Add(Modifier);
            hashCode.Add(Comparator);
            hashCode.Add(ComponentIndex);
            hashCode.Add(Value);
        }

        /// <inheritdoc />
        public override bool ValueInsensitiveEquals(Expression other)
        {
            return other is SearchParameterPredicateExpression predicate &&
                predicate.Parameter.Equals(Parameter) &&
                predicate.Modifier == Modifier &&
                predicate.Comparator == Comparator &&
                predicate.ComponentIndex == ComponentIndex &&
                predicate.Value.Equals(Value);
        }
    }
}
```

- [ ] **Step 4: Extend the visitor contract with a compatibility guard**

Add `using System;` to `IExpressionVisitor.cs`.

Add this method immediately after `VisitSearchParameter` in `IExpressionVisitor.cs`:

```csharp
/// <summary>
/// Visits a semantic <see cref="SearchParameterPredicateExpression"/>.
/// </summary>
/// <param name="expression">The expression to visit.</param>
/// <param name="context">The input.</param>
TOutput VisitSearchParameterPredicate(SearchParameterPredicateExpression expression, TContext context) =>
    throw new InvalidOperationException("Semantic search predicates must be lowered before visiting a legacy expression tree.");
```

The default interface body lets the two direct SQL/Cosmos visitor implementations compile unchanged, but fails explicitly if a semantic leaf accidentally reaches either backend.

Override the guard in `DefaultExpressionVisitor` so analytical visitors retain their existing leaf behavior:

```csharp
public virtual TOutput VisitSearchParameterPredicate(SearchParameterPredicateExpression expression, TContext context) => default;
```

Add this method to `ExpressionRewriter`:

```csharp
public virtual Expression VisitSearchParameterPredicate(SearchParameterPredicateExpression expression, TContext context)
{
    return expression;
}
```

- [ ] **Step 5: Run the semantic-leaf test**

```powershell
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~GivenDateEquality_WhenSemanticPredicateIsConstructed" --no-restore --verbosity quiet
```

Expected: PASS with one test.

- [ ] **Step 6: Run a Core build**

```powershell
dotnet build "src\Microsoft.Health.Fhir.Core\Microsoft.Health.Fhir.Core.csproj" --framework net9.0 --no-restore -warnaserror
```

Expected: PASS. Visitors derived from `DefaultExpressionVisitor` or `ExpressionRewriter` receive their explicit semantic-leaf behavior, while direct legacy visitors inherit the interface guard.

- [ ] **Step 7: Commit the semantic leaf**

```powershell
git add "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\SearchParameterPredicateExpression.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\IExpressionVisitor.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\DefaultExpressionVisitor.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\ExpressionRewriter.cs" "src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\Expressions\Parsers\SearchValueExpressionBuilderTests.cs"
git commit -m "Add semantic FHIR search predicate expression" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Task 2: Add the single legacy-lowering boundary

**Files:**
- Create: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/LegacyExpressionLowerer.cs`
- Create: `src/Microsoft.Health.Fhir.Core.UnitTests/Features/Search/Expressions/LegacyExpressionLowererTests.cs`

- [ ] **Step 1: Write failing date and token-text lowering tests**

Create `LegacyExpressionLowererTests.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;
using SearchComparator = Microsoft.Health.Fhir.ValueSets.SearchComparator;
using SearchModifierCode = Microsoft.Health.Fhir.ValueSets.SearchModifierCode;
using SearchParamType = Microsoft.Health.Fhir.ValueSets.SearchParamType;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.Expressions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public class LegacyExpressionLowererTests
    {
        [Fact]
        public void GivenSemanticDateEquality_WhenLowered_ThenLegacyRangeFieldsAreProduced()
        {
            var parameter = new SearchParameterInfo("birthdate", "birthdate", SearchParamType.Date);
            var semantic = new SearchParameterExpression(
                parameter,
                new SearchParameterPredicateExpression(
                    parameter,
                    modifier: null,
                    SearchComparator.Eq,
                    componentIndex: null,
                    DateTimeSearchValue.Parse("2026-07")));

            Expression lowered = LegacyExpressionLowerer.Instance.Lower(semantic);

            var wrapper = Assert.IsType<SearchParameterExpression>(lowered);
            var and = Assert.IsType<MultiaryExpression>(wrapper.Expression);
            Assert.Equal(MultiaryOperator.And, and.MultiaryOperation);
            Assert.Collection(
                and.Expressions,
                expression =>
                {
                    var lowerBound = Assert.IsType<BinaryExpression>(expression);
                    Assert.Equal(BinaryOperator.GreaterThanOrEqual, lowerBound.BinaryOperator);
                    Assert.Equal(FieldName.DateTimeStart, lowerBound.FieldName);
                },
                expression =>
                {
                    var upperBound = Assert.IsType<BinaryExpression>(expression);
                    Assert.Equal(BinaryOperator.LessThanOrEqual, upperBound.BinaryOperator);
                    Assert.Equal(FieldName.DateTimeEnd, upperBound.FieldName);
                });
        }

        [Fact]
        public void GivenSemanticTokenTextModifier_WhenLowered_ThenLegacyTokenTextPredicateIsProduced()
        {
            var parameter = new SearchParameterInfo("code", "code", SearchParamType.Token);
            var semantic = new SearchParameterExpression(
                parameter,
                new SearchParameterPredicateExpression(
                    parameter,
                    new SearchModifier(SearchModifierCode.Text),
                    SearchComparator.Eq,
                    componentIndex: null,
                    new TokenSearchValue(system: null, code: null, text: @"heart\,lung")));

            Expression lowered = LegacyExpressionLowerer.Instance.Lower(semantic);

            var wrapper = Assert.IsType<SearchParameterExpression>(lowered);
            var text = Assert.IsType<StringExpression>(wrapper.Expression);
            Assert.Equal(StringOperator.StartsWith, text.StringOperator);
            Assert.Equal(FieldName.TokenText, text.FieldName);
            Assert.Equal(@"heart\,lung", text.Value);
            Assert.True(text.IgnoreCase);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~LegacyExpressionLowererTests" --no-restore --verbosity quiet
```

Expected: FAIL to compile because `LegacyExpressionLowerer` does not exist.

- [ ] **Step 3: Implement the compatibility lowerer**

Create `LegacyExpressionLowerer.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.Core.Features.Search.Expressions
{
    /// <summary>
    /// Lowers semantic search predicates into the field-level expression model used by legacy backends.
    /// </summary>
    public sealed class LegacyExpressionLowerer : ExpressionRewriter<object>
    {
        private LegacyExpressionLowerer()
        {
        }

        /// <summary>
        /// Gets the stateless lowerer instance.
        /// </summary>
        public static LegacyExpressionLowerer Instance { get; } = new LegacyExpressionLowerer();

        /// <summary>
        /// Lowers a semantic expression tree.
        /// </summary>
        public Expression Lower(Expression expression)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            return expression.AcceptVisitor(this, context: null);
        }

        /// <inheritdoc />
        public override Expression VisitSearchParameterPredicate(SearchParameterPredicateExpression expression, object context)
        {
            if (expression.Modifier?.SearchModifierCode == SearchModifierCode.Text)
            {
                if (expression.Value is not TokenSearchValue token || token.Text == null)
                {
                    throw new InvalidOperationException("The token :text modifier requires a token text search value.");
                }

                return Expression.StartsWith(FieldName.TokenText, expression.ComponentIndex, token.Text, true);
            }

            return new SearchValueExpressionBuilderHelper().Build(
                expression.Parameter.Code,
                expression.Modifier,
                expression.Comparator,
                expression.ComponentIndex,
                expression.Value);
        }
    }
}
```

- [ ] **Step 4: Run the lowerer tests**

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~LegacyExpressionLowererTests" --no-restore --verbosity quiet
```

Expected: PASS with two tests.

- [ ] **Step 5: Commit the lowerer**

```powershell
git add "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\LegacyExpressionLowerer.cs" "src\Microsoft.Health.Fhir.Core.UnitTests\Features\Search\Expressions\LegacyExpressionLowererTests.cs"
git commit -m "Add legacy search expression lowerer" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Task 3: Make search-parameter parsing semantic-first

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/ISearchParameterExpressionParser.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/SearchParameterExpressionParser.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/Expressions/Parsers/SearchValueExpressionBuilderTests.cs`

- [ ] **Step 1: Expose the semantic parser API**

Add to `ISearchParameterExpressionParser`:

```csharp
/// <summary>
/// Parses a search parameter into a semantic expression without lowering it to storage fields.
/// </summary>
/// <param name="searchParameter">The resolved search parameter.</param>
/// <param name="modifier">The FHIR search modifier.</param>
/// <param name="value">The raw search value.</param>
/// <returns>The semantic expression.</returns>
Expression ParseSemantic(
    SearchParameterInfo searchParameter,
    SearchModifier modifier,
    string value);
```

Temporarily add this method to `SearchParameterExpressionParser`; Step 5 replaces its body:

```csharp
/// <inheritdoc />
public Expression ParseSemantic(SearchParameterInfo searchParameter, SearchModifier modifier, string value)
{
    throw new NotImplementedException();
}
```

- [ ] **Step 2: Add semantic tests for modifiers, composites, and OR values**

Add these tests to `SearchValueExpressionBuilderTests`:

```csharp
[Fact]
public void GivenStringContains_WhenParsedSemantically_ThenModifierIsPreserved()
{
    SearchParameterInfo parameter = CreateSearchParameter(SearchParamType.String);
    var modifier = new SearchModifier(SearchModifierCode.Contains);

    var wrapper = Assert.IsType<SearchParameterExpression>(_parser.ParseSemantic(parameter, modifier, "Seattle"));
    var predicate = Assert.IsType<SearchParameterPredicateExpression>(wrapper.Expression);

    Assert.Same(parameter, predicate.Parameter);
    Assert.Same(modifier, predicate.Modifier);
    Assert.Equal(SearchComparator.Eq, predicate.Comparator);
    Assert.Equal("Seattle", Assert.IsType<StringSearchValue>(predicate.Value).String);
}

[Fact]
public void GivenMultipleValues_WhenParsedSemantically_ThenOrContainsSemanticPredicates()
{
    SearchParameterInfo parameter = CreateSearchParameter(SearchParamType.String);

    var wrapper = Assert.IsType<SearchParameterExpression>(_parser.ParseSemantic(parameter, modifier: null, "one,two"));
    var or = Assert.IsType<MultiaryExpression>(wrapper.Expression);

    Assert.Equal(MultiaryOperator.Or, or.MultiaryOperation);
    Assert.Collection(
        or.Expressions,
        expression => Assert.Equal("one", Assert.IsType<StringSearchValue>(Assert.IsType<SearchParameterPredicateExpression>(expression).Value).String),
        expression => Assert.Equal("two", Assert.IsType<StringSearchValue>(Assert.IsType<SearchParameterPredicateExpression>(expression).Value).String));
}

[Fact]
public void GivenMultipleTokenValuesWithNot_WhenParsedSemantically_ThenNotWrapsUnmodifiedPredicates()
{
    SearchParameterInfo parameter = CreateSearchParameter(SearchParamType.Token);
    var modifier = new SearchModifier(SearchModifierCode.Not);

    var wrapper = Assert.IsType<SearchParameterExpression>(_parser.ParseSemantic(parameter, modifier, "one,two"));
    var not = Assert.IsType<NotExpression>(wrapper.Expression);
    var or = Assert.IsType<MultiaryExpression>(not.Expression);

    Assert.All(
        or.Expressions,
        expression => Assert.Null(Assert.IsType<SearchParameterPredicateExpression>(expression).Modifier));
}

[Fact]
public void GivenCompositeValues_WhenParsedSemantically_ThenComponentsRetainTheirSearchParametersAndPositions()
{
    var token = new SearchParameterInfo("code", "code", ValueSets.SearchParamType.Token);
    var quantity = new SearchParameterInfo("value", "value", ValueSets.SearchParamType.Quantity);
    var parameter = new SearchParameterInfo(
        DefaultParamName,
        DefaultParamName,
        ValueSets.SearchParamType.Composite,
        components: new[]
        {
            new SearchParameterComponentInfo(new Uri("http://code")) { ResolvedSearchParameter = token },
            new SearchParameterComponentInfo(new Uri("http://value")) { ResolvedSearchParameter = quantity },
        });

    var wrapper = Assert.IsType<SearchParameterExpression>(
        _parser.ParseSemantic(parameter, modifier: null, "system|code$10|system|mg"));
    var and = Assert.IsType<MultiaryExpression>(wrapper.Expression);

    Assert.Collection(
        and.Expressions,
        expression =>
        {
            var predicate = Assert.IsType<SearchParameterPredicateExpression>(expression);
            Assert.Same(token, predicate.Parameter);
            Assert.Equal(0, predicate.ComponentIndex);
            Assert.IsType<TokenSearchValue>(predicate.Value);
        },
        expression =>
        {
            var predicate = Assert.IsType<SearchParameterPredicateExpression>(expression);
            Assert.Same(quantity, predicate.Parameter);
            Assert.Equal(1, predicate.ComponentIndex);
            Assert.IsType<QuantitySearchValue>(predicate.Value);
        });
}
```

- [ ] **Step 3: Run semantic tests to verify failure**

```powershell
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~ParsedSemantically" --no-restore --verbosity quiet
```

Expected: FAIL because `ParseSemantic` still throws `NotImplementedException`.

- [ ] **Step 4: Make the existing API lower semantic output**

Replace the body of `Parse` with:

```csharp
public Expression Parse(
    SearchParameterInfo searchParameter,
    SearchModifier modifier,
    string value)
{
    return LegacyExpressionLowerer.Instance.Lower(ParseSemantic(searchParameter, modifier, value));
}
```

- [ ] **Step 5: Implement semantic parsing**

Move the current validation and parsing logic into `ParseSemantic`. Preserve the existing control flow, with these exact substitutions:

1. Keep `:missing` returning `Expression.MissingSearchParameter(searchParameter, isMissing)`.
2. Replace the `:text` field expression with the following semantic predicate. Use the `Text` slot of `TokenSearchValue` so the semantic value remains token-typed and the compatibility lowerer can reproduce the raw legacy field value exactly, including escape sequences.

```csharp
outputExpression = new SearchParameterPredicateExpression(
    searchParameter,
    modifier,
    SearchComparator.Eq,
    componentIndex: null,
    new TokenSearchValue(system: null, code: null, text: value));
```

3. Rename `Build` to `BuildSemantic`, update both call sites, and remove the `SearchValueExpressionBuilderHelper` allocation.
4. Keep comparator detection before comma splitting exactly where it is today. Only the unsplit single value uses the comparator-stripped `valueSpan`; comma-separated parts are parsed literally, and a comparator on the full value still raises `SearchComparatorNotSupported`.
5. For a single value, return:

```csharp
ISearchValue searchValue = parser(valueSpan.ToString());
searchValue = ApplyTargetTypeModifier(modifier, searchValue);

return new SearchParameterPredicateExpression(
    searchParameter,
    modifier,
    comparator,
    componentIndex,
    searchValue);
```

6. Replace the current multiple-value branch with:

```csharp
if (comparator != SearchComparator.Eq)
{
    throw new InvalidSearchOperationException(Core.Resources.SearchComparatorNotSupported);
}

if (modifier?.SearchModifierCode == SearchModifierCode.Not)
{
    Expression[] expressions = parts.Select(part =>
        new SearchParameterPredicateExpression(
            searchParameter,
            modifier: null,
            comparator,
            componentIndex,
            parser(part))).ToArray();

    return Expression.Not(Expression.Or(expressions));
}

Expression[] orExpressions = parts.Select(part =>
{
    ISearchValue searchValue = parser(part);
    searchValue = ApplyTargetTypeModifier(modifier, searchValue);

    return new SearchParameterPredicateExpression(
        searchParameter,
        modifier,
        comparator,
        componentIndex,
        searchValue);
}).ToArray();

return Expression.Or(orExpressions);
```

7. Keep the existing local `ApplyTargetTypeModifier` function unchanged.
8. Keep the final outer wrapper:

```csharp
return Expression.SearchParameter(searchParameter, outputExpression);
```

Do not alter `:missing` validation, token `:text` validation, composite arity validation, reference target-type validation, or exception messages. For composites, the existing `Build`-to-`BuildSemantic` call-site change ensures each inner predicate receives the resolved component `SearchParameterInfo` and zero-based component index while the outer `SearchParameterExpression` remains unchanged.

- [ ] **Step 6: Prove semantic-to-legacy equivalence for representative values**

Add this theory to `SearchValueExpressionBuilderTests`:

```csharp
[Theory]
[InlineData(SearchParamType.Date, null, "eq2026-07")]
[InlineData(SearchParamType.Date, null, "ge2026")]
[InlineData(SearchParamType.Number, null, "lt10")]
[InlineData(SearchParamType.Quantity, null, "10|http://unitsofmeasure.org|mg")]
[InlineData(SearchParamType.String, SearchModifierCode.Contains, "sea")]
[InlineData(SearchParamType.Token, null, "system|code")]
[InlineData(SearchParamType.Token, SearchModifierCode.Text, @"heart\,lung")]
[InlineData(SearchParamType.Uri, SearchModifierCode.Above, "http://example.org/a")]
public void GivenSupportedValue_WhenSemanticExpressionIsLowered_ThenItEqualsTheLegacyParseResult(
    SearchParamType type,
    SearchModifierCode? modifierCode,
    string value)
{
    SearchParameterInfo parameter = CreateSearchParameter(type);
    SearchModifier modifier = modifierCode.HasValue ? new SearchModifier(modifierCode.Value) : null;

    Expression semantic = _parser.ParseSemantic(parameter, modifier, value);
    Expression lowered = LegacyExpressionLowerer.Instance.Lower(semantic);
    Expression legacy = _parser.Parse(parameter, modifier, value);

    Assert.Equal(legacy.ToString(), lowered.ToString());
}
```

- [ ] **Step 7: Run all existing search-value expression tests**

```powershell
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
```

Expected: all pre-existing tests and new semantic tests pass. Existing tests prove the legacy `Parse` output is unchanged.

- [ ] **Step 8: Run the same tests for every FHIR version**

```powershell
dotnet test "src\Microsoft.Health.Fhir.Stu3.Core.UnitTests\Microsoft.Health.Fhir.Stu3.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4B.Core.UnitTests\Microsoft.Health.Fhir.R4B.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R5.Core.UnitTests\Microsoft.Health.Fhir.R5.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
```

Expected: PASS for STU3, R4, R4B, and R5.

- [ ] **Step 9: Commit semantic-first parsing**

```powershell
git add "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\Parsers\ISearchParameterExpressionParser.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\Parsers\SearchParameterExpressionParser.cs" "src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\Expressions\Parsers\SearchValueExpressionBuilderTests.cs"
git commit -m "Parse search parameters into semantic expressions" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Task 4: Document and verify the compatibility boundary

**Files:**
- Modify: `docs/SearchArchitecture.md`

- [ ] **Step 1: Document semantic-first parsing**

Insert this section before `## Extraction`:

```markdown
## Request compilation

Search-parameter parsing first produces semantic Core expressions. A semantic predicate retains the resolved SearchParameter, FHIR comparator and modifier, composite position, and normalized `ISearchValue`; it does not refer to persisted field names or backend schema.

During the strangler migration, `LegacyExpressionLowerer` immediately converts semantic predicates into the existing `FieldName`, `BinaryExpression`, and `StringExpression` representation returned by the public parser API. SQL Server, Cosmos DB, member match, and other current consumers therefore keep their existing behavior. The next migration stage will retain complete semantic trees on `SearchOptions` for logical planning.
```

- [ ] **Step 2: Format changed C# files and check the diff**

```powershell
dotnet format "Microsoft.Health.Fhir.sln" --no-restore --include "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\SearchParameterPredicateExpression.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\LegacyExpressionLowerer.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\IExpressionVisitor.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\DefaultExpressionVisitor.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\ExpressionRewriter.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\Parsers\ISearchParameterExpressionParser.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\Parsers\SearchParameterExpressionParser.cs" "src\Microsoft.Health.Fhir.Core.UnitTests\Features\Search\Expressions\LegacyExpressionLowererTests.cs" "src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\Expressions\Parsers\SearchValueExpressionBuilderTests.cs"
git --no-pager diff --check
```

Expected: both commands exit with code 0; `git diff --check` prints no errors.

- [ ] **Step 3: Build the solution**

```powershell
dotnet build "Microsoft.Health.Fhir.sln" --configuration Debug --framework net9.0 --no-restore -warnaserror
```

Expected: build succeeds with zero warnings and zero errors.

- [ ] **Step 4: Run focused parser and lowerer tests**

```powershell
dotnet test "src\Microsoft.Health.Fhir.Core.UnitTests\Microsoft.Health.Fhir.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~LegacyExpressionLowererTests" --no-build --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.Stu3.Core.UnitTests\Microsoft.Health.Fhir.Stu3.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests|FullyQualifiedName~ExpressionParserTests" --no-build --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests|FullyQualifiedName~ExpressionParserTests" --no-build --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4B.Core.UnitTests\Microsoft.Health.Fhir.R4B.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests|FullyQualifiedName~ExpressionParserTests" --no-build --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R5.Core.UnitTests\Microsoft.Health.Fhir.R5.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests|FullyQualifiedName~ExpressionParserTests" --no-build --verbosity quiet
```

Expected: every command passes with zero failed tests.

- [ ] **Step 5: Verify backend code is untouched**

```powershell
git --no-pager diff -- "src\Microsoft.Health.Fhir.SqlServer" "src\Microsoft.Health.Fhir.CosmosDb"
```

Expected: no output.

- [ ] **Step 6: Commit documentation**

```powershell
git add "docs\SearchArchitecture.md"
git commit -m "Document semantic search expression lowering" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Completion criteria

Plan 1 is complete only when:

- `ParseSemantic` preserves FHIR comparator, modifier, normalized value, SearchParameter identity, and composite position;
- `Parse` remains the same compatibility API and returns the same legacy expression shapes;
- `SearchValueExpressionBuilderHelper` is invoked only by `LegacyExpressionLowerer`;
- no raw query syntax model is added;
- SQL Server and Cosmos code remain unchanged;
- existing parser tests pass for STU3, R4, R4B, and R5;
- the solution builds for `net9.0` with warnings treated as errors.
