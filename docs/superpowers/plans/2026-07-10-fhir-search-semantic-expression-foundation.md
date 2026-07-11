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
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/ExpressionRewriter.cs` — preserves semantic leaves unless overridden.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/ISearchParameterExpressionParser.cs` — exposes `ParseSemantic`.
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/SearchParameterExpressionParser.cs` — constructs semantic leaves, then lowers for the existing API.
- `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/Expressions/Parsers/SearchValueExpressionBuilderTests.cs` — semantic-shape and compatibility tests.
- `docs/SearchArchitecture.md` — documents semantic construction and compatibility lowering.

## Task 1: Add the semantic search-predicate leaf

**Files:**
- Create: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/SearchParameterPredicateExpression.cs`
- Modify: `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/IExpressionVisitor.cs`
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
    /// Represents a semantic FHIR search predicate that captures a search parameter, an optional
    /// modifier, a comparator, an optional component index, and a normalized search value.
    /// This node operates at the semantic level and must be lowered to legacy SQL/Cosmos tree nodes
    /// before reaching a backend expression visitor.
    /// </summary>
    public sealed class SearchParameterPredicateExpression : Expression
    {
        public SearchParameterPredicateExpression(
            SearchParameterInfo parameter,
            SearchModifier modifier,
            SearchComparator comparator,
            int? componentIndex,
            ISearchValue value)
        {
            EnsureArg.IsNotNull(parameter, nameof(parameter));
            EnsureArg.IsNotNull(value, nameof(value));

            if (!Enum.IsDefined<SearchComparator>(comparator))
            {
                throw new ArgumentOutOfRangeException(nameof(comparator), comparator, "SearchComparator value is not defined.");
            }

            if (componentIndex.HasValue && componentIndex.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(componentIndex), componentIndex, "Component index must not be negative.");
            }

            Parameter = parameter;
            Modifier = modifier;
            Comparator = comparator;
            ComponentIndex = componentIndex;
            Value = value;
        }

        public SearchParameterInfo Parameter { get; }
        public SearchModifier Modifier { get; }
        public SearchComparator Comparator { get; }
        public int? ComponentIndex { get; }
        public ISearchValue Value { get; }

        public override TOutput AcceptVisitor<TContext, TOutput>(IExpressionVisitor<TContext, TOutput> visitor, TContext context)
        {
            EnsureArg.IsNotNull(visitor, nameof(visitor));
            return visitor.VisitSearchParameterPredicate(this, context);
        }

        public override string ToString()
        {
            string componentPart = ComponentIndex.HasValue ? $"[{ComponentIndex}]." : string.Empty;
            string modifierPart = Modifier != null ? $":{Modifier}" : string.Empty;

            // TokenSearchValue.ToString() does not render the Text property; use it directly when present.
            string valuePart = Value is TokenSearchValue tsv && tsv.Text != null
                ? tsv.Text
                : Value.ToString();

            return $"(SemanticPredicate {componentPart}{Parameter.Code}{modifierPart} {Comparator} {valuePart})";
        }

        public override void AddValueInsensitiveHashCode(ref HashCode hashCode)
        {
            hashCode.Add(typeof(SearchParameterPredicateExpression));
            hashCode.Add(Parameter);
            hashCode.Add(Modifier);
            hashCode.Add(Comparator);
            hashCode.Add(ComponentIndex);
            hashCode.Add(Value);
        }

        public override bool ValueInsensitiveEquals(Expression other)
        {
            return other is SearchParameterPredicateExpression sppe &&
                   sppe.Parameter.Equals(Parameter) &&
                   sppe.Modifier == Modifier &&
                   sppe.Comparator == Comparator &&
                   sppe.ComponentIndex == ComponentIndex &&
                   (sppe.Value?.Equals(Value) ?? Value == null);
        }
    }
}
```

- [ ] **Step 4: Extend the visitor contract with a compatibility guard**

Add this method immediately after `VisitSearchParameter` in `IExpressionVisitor.cs`:

```csharp
/// <summary>
/// Visits a semantic <see cref="SearchParameterPredicateExpression"/>.
/// Implementations that operate on a fully-lowered legacy tree should not encounter this node;
/// the default implementation throws <see cref="InvalidOperationException"/> to surface
/// any misrouting at runtime while keeping existing SQL/Cosmos visitors compile-compatible.
/// </summary>
/// <param name="expression">The semantic predicate expression to visit.</param>
/// <param name="context">The input.</param>
TOutput VisitSearchParameterPredicate(SearchParameterPredicateExpression expression, TContext context) =>
    throw new InvalidOperationException("Semantic search predicates must be lowered before visiting a legacy expression tree.");
```

The default interface body lets the two direct SQL/Cosmos visitor implementations compile unchanged, but fails explicitly if a semantic leaf accidentally reaches either backend. **Do not override this method in `DefaultExpressionVisitor`.** Semantic leaves must not be silently omitted from legacy visitors.

Add this override to `ExpressionRewriter` to preserve semantic leaves when rewriting (identity transformation for intentional semantic rewriters):

```csharp
public virtual Expression VisitSearchParameterPredicate(SearchParameterPredicateExpression expression, TContext context)
{
    return expression;
}
```

- [ ] **Step 4b: Add a regression test for the visitor guard**

Add this test to `SearchValueExpressionBuilderTests` to verify that a `DefaultExpressionVisitor` subclass cannot silently omit semantic predicates:

```csharp
[Fact]
public void GivenSemanticPredicate_WhenDispatchedToDefaultExpressionVisitorSubclass_ThenInvalidOperationExceptionIsThrown()
{
    // Arrange
    SearchParameterInfo searchParameter = CreateSearchParameter(SearchParamType.Date);
    var value = DateTimeSearchValue.Parse("2026-07");
    var predicate = new SearchParameterPredicateExpression(
        searchParameter,
        modifier: null,
        SearchComparator.Eq,
        componentIndex: null,
        value);

    // A minimal subclass that overrides nothing; any legacy SQL/Cosmos visitor is an instance of this.
    var visitor = new MinimalDefaultExpressionVisitor();

    // Act & Assert
    var ex = Assert.Throws<InvalidOperationException>(
        () => predicate.AcceptVisitor(visitor, context: null));
    Assert.Contains("lowered", ex.Message, StringComparison.OrdinalIgnoreCase);
}

// Helper visitor for regression testing
private sealed class MinimalDefaultExpressionVisitor : DefaultExpressionVisitor<object, object>
{
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

Expected: PASS. Visitors derived from `ExpressionRewriter` receive the explicit identity override. Visitors derived from `DefaultExpressionVisitor` and direct `IExpressionVisitor` implementors inherit the interface guard that throws `InvalidOperationException`.

- [ ] **Step 7: Commit the semantic leaf**

```powershell
git add "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\SearchParameterPredicateExpression.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\IExpressionVisitor.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\ExpressionRewriter.cs" "src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\Expressions\Parsers\SearchValueExpressionBuilderTests.cs"
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

using System;
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
        /// <summary>
        /// A SearchParameterExpression wrapping a SearchParameterPredicateExpression with
        /// Eq and a partial-month DateTimeSearchValue should lower to a SearchParameterExpression
        /// whose inner expression is an And MultiaryExpression with two BinaryExpression leaves:
        /// - GreaterThanOrEqual on FieldName.DateTimeStart
        /// - LessThanOrEqual on FieldName.DateTimeEnd
        /// </summary>
        [Fact]
        public void GivenSemanticDateEqualityPredicate_WhenLowered_ProducesDateRangeBinaryExpressions()
        {
            // Arrange
            var param = new SearchParameterInfo("birthdate", "birthdate", SearchParamType.Date);
            var dateValue = DateTimeSearchValue.Parse("2026-07");
            var predicate = new SearchParameterPredicateExpression(
                parameter: param,
                modifier: null,
                comparator: SearchComparator.Eq,
                componentIndex: null,
                value: dateValue);
            var wrapper = new SearchParameterExpression(param, predicate);

            // Act
            var result = LegacyExpressionLowerer.Instance.Lower(wrapper);

            // Assert – outer wrapper preserved
            var spe = Assert.IsType<SearchParameterExpression>(result);
            Assert.Same(param, spe.Parameter);

            // Inner expression must be an And multiary
            var and = Assert.IsType<MultiaryExpression>(spe.Expression);
            Assert.Equal(MultiaryOperator.And, and.MultiaryOperation);
            Assert.Equal(2, and.Expressions.Count);

            // First leaf: GreaterThanOrEqual on DateTimeStart
            var lower = Assert.IsType<BinaryExpression>(and.Expressions[0]);
            Assert.Equal(BinaryOperator.GreaterThanOrEqual, lower.BinaryOperator);
            Assert.Equal(FieldName.DateTimeStart, lower.FieldName);
            Assert.Null(lower.ComponentIndex);

            // Second leaf: LessThanOrEqual on DateTimeEnd
            var upper = Assert.IsType<BinaryExpression>(and.Expressions[1]);
            Assert.Equal(BinaryOperator.LessThanOrEqual, upper.BinaryOperator);
            Assert.Equal(FieldName.DateTimeEnd, upper.FieldName);
            Assert.Null(upper.ComponentIndex);
        }

        /// <summary>
        /// A Text-modifier predicate on a Token parameter with a raw text value (including escapes)
        /// should lower to a StartsWith on FieldName.TokenText with IgnoreCase=true.
        /// </summary>
        [Fact]
        public void GivenSemanticTokenTextPredicate_WhenLowered_ProducesTokenTextStringExpression()
        {
            // Arrange
            var param = new SearchParameterInfo("code", "code", SearchParamType.Token);
            var tokenValue = new TokenSearchValue(system: null, code: null, text: @"heart\,lung");
            var predicate = new SearchParameterPredicateExpression(
                parameter: param,
                modifier: new SearchModifier(SearchModifierCode.Text),
                comparator: SearchComparator.Eq,
                componentIndex: null,
                value: tokenValue);
            var wrapper = new SearchParameterExpression(param, predicate);

            // Act
            var result = LegacyExpressionLowerer.Instance.Lower(wrapper);

            // Assert
            var spe = Assert.IsType<SearchParameterExpression>(result);
            var str = Assert.IsType<StringExpression>(spe.Expression);
            Assert.Equal(StringOperator.StartsWith, str.StringOperator);
            Assert.Equal(FieldName.TokenText, str.FieldName);
            Assert.Equal(@"heart\,lung", str.Value);
            Assert.True(str.IgnoreCase);
        }

        /// <summary>
        /// A Text-modifier predicate on a non-Token parameter is a semantic invariant violation
        /// and must throw InvalidOperationException.
        /// </summary>
        [Fact]
        public void GivenSemanticTextPredicateOnNonTokenParameter_WhenLowered_ThrowsInvalidOperationException()
        {
            // Arrange
            var param = new SearchParameterInfo("status", "status", SearchParamType.String);
            var stringValue = StringSearchValue.Parse("active");
            var predicate = new SearchParameterPredicateExpression(
                parameter: param,
                modifier: new SearchModifier(SearchModifierCode.Text),
                comparator: SearchComparator.Eq,
                componentIndex: null,
                value: stringValue);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LegacyExpressionLowerer.Instance.Lower(predicate));
            Assert.Contains("Text", ex.Message);
            Assert.Contains("Token", ex.Message);
        }

        /// <summary>
        /// A Text-modifier predicate on a Token parameter with a non-Eq comparator is a
        /// semantic invariant violation and must throw InvalidOperationException.
        /// </summary>
        [Fact]
        public void GivenSemanticTextPredicateWithNonEqComparator_WhenLowered_ThrowsInvalidOperationException()
        {
            // Arrange
            var param = new SearchParameterInfo("code", "code", SearchParamType.Token);
            var tokenValue = new TokenSearchValue(system: null, code: null, text: "heart");
            var predicate = new SearchParameterPredicateExpression(
                parameter: param,
                modifier: new SearchModifier(SearchModifierCode.Text),
                comparator: SearchComparator.Gt,  // Invalid for :text
                componentIndex: null,
                value: tokenValue);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LegacyExpressionLowerer.Instance.Lower(predicate));
            Assert.Contains("Text", ex.Message);
            Assert.Contains("Eq", ex.Message);
        }

        /// <summary>
        /// A Text-modifier predicate on a Token parameter with a TokenSearchValue that has no
        /// Text property (null) is a semantic invariant violation and must throw InvalidOperationException.
        /// </summary>
        [Fact]
        public void GivenSemanticTextPredicateWithNullText_WhenLowered_ThrowsInvalidOperationException()
        {
            // Arrange
            var param = new SearchParameterInfo("code", "code", SearchParamType.Token);
            var tokenValue = new TokenSearchValue(system: "http://example.org", code: "123", text: null);
            var predicate = new SearchParameterPredicateExpression(
                parameter: param,
                modifier: new SearchModifier(SearchModifierCode.Text),
                comparator: SearchComparator.Eq,
                componentIndex: null,
                value: tokenValue);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LegacyExpressionLowerer.Instance.Lower(predicate));
            Assert.Contains("Text", ex.Message);
            Assert.Contains("TokenSearchValue", ex.Message);
        }

        /// <summary>
        /// A Text-modifier predicate with a raw text value containing escape sequences should be
        /// preserved exactly through the lowering pass, including escape sequences.
        /// </summary>
        [Fact]
        public void GivenSemanticTokenTextWithEscapes_WhenLowered_EscapesArePreserved()
        {
            // Arrange
            const string rawTextWithEscapes = @"test\,value\$other";
            var param = new SearchParameterInfo("code", "code", SearchParamType.Token);
            var tokenValue = new TokenSearchValue(system: null, code: null, text: rawTextWithEscapes);
            var predicate = new SearchParameterPredicateExpression(
                parameter: param,
                modifier: new SearchModifier(SearchModifierCode.Text),
                comparator: SearchComparator.Eq,
                componentIndex: null,
                value: tokenValue);
            var wrapper = new SearchParameterExpression(param, predicate);

            // Act
            var result = LegacyExpressionLowerer.Instance.Lower(wrapper);

            // Assert
            var spe = Assert.IsType<SearchParameterExpression>(result);
            var str = Assert.IsType<StringExpression>(spe.Expression);
            Assert.Equal(rawTextWithEscapes, str.Value);
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
    /// A one-way compatibility boundary that lowers semantic SearchParameterPredicateExpression
    /// leaves into the legacy field-level expression tree understood by the SQL and Cosmos backends.
    /// All structural nodes (multiary, chained, not, sort, etc.) are preserved transparently.
    /// The only special case is the :text modifier on token parameters, because
    /// SearchValueExpressionBuilderHelper does not accept token-text semantics.
    /// </summary>
    public sealed class LegacyExpressionLowerer : ExpressionRewriter<object>
    {
        /// <summary>
        /// Gets the singleton instance of LegacyExpressionLowerer.
        /// </summary>
        public static readonly LegacyExpressionLowerer Instance = new LegacyExpressionLowerer();

        private LegacyExpressionLowerer()
        {
        }

        /// <summary>
        /// Lowers all SearchParameterPredicateExpression nodes within the expression tree
        /// into legacy field-level expression nodes.
        /// </summary>
        /// <param name="expression">The expression tree to lower. Must not be null.</param>
        /// <returns>An equivalent expression tree containing only legacy field-level nodes.</returns>
        /// <exception cref="ArgumentNullException">Thrown when expression is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a SearchParameterPredicateExpression with the :text modifier violates
        /// an internal semantic invariant: the parameter type is not Token, the comparator is not Eq,
        /// or the value is not a TokenSearchValue with a non-null Text property.
        /// </exception>
        public Expression Lower(Expression expression)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));
            return expression.AcceptVisitor(this, context: null);
        }

        /// <inheritdoc />
        public override Expression VisitSearchParameterPredicate(SearchParameterPredicateExpression expression, object context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            // Special case: :text modifier on token parameters.
            // SearchValueExpressionBuilderHelper does not handle token-text semantics,
            // so we translate directly to a StartsWith on the TokenText field.
            // All three invariants must hold; any violation is an internal semantic error.
            if (expression.Modifier?.SearchModifierCode == SearchModifierCode.Text)
            {
                if (expression.Parameter.Type != SearchParamType.Token)
                {
                    throw new InvalidOperationException(
                        $"Invariant violation: the '{SearchModifierCode.Text}' modifier is only valid on Token " +
                        $"parameters, but parameter '{expression.Parameter.Code}' has type '{expression.Parameter.Type}'.");
                }

                if (expression.Comparator != SearchComparator.Eq)
                {
                    throw new InvalidOperationException(
                        $"Invariant violation: the '{SearchModifierCode.Text}' modifier only supports the " +
                        $"'{SearchComparator.Eq}' comparator, but parameter '{expression.Parameter.Code}' " +
                        $"uses '{expression.Comparator}'.");
                }

                if (expression.Value is not TokenSearchValue token || token.Text == null)
                {
                    throw new InvalidOperationException(
                        $"Invariant violation: a '{SearchModifierCode.Text}' predicate on parameter " +
                        $"'{expression.Parameter.Code}' must carry a TokenSearchValue with a non-null Text property.");
                }

                return Expression.StartsWith(FieldName.TokenText, expression.ComponentIndex, token.Text, true);
            }

            // All other cases are delegated to the authoritative mapping helper.
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

Expected: PASS with multiple lowerer tests.

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

- [ ] **Step 5: Implement semantic parsing with input-order validation**

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

3. Rename the `Build` method to `BuildSemantic`, and update all call sites. Do not allocate `SearchValueExpressionBuilderHelper`.

4. Keep comparator detection before comma splitting exactly where it is today. Only the unsplit single value uses the comparator-stripped `valueSpan`; comma-separated parts are parsed literally.

5. For a single value, after parsing and target-type-modifier adjustment, **validate before returning**:

```csharp
ISearchValue searchValue = parser(valueSpan.ToString());
searchValue = ApplyTargetTypeModifier(modifier, searchValue);
ValidateSemanticPredicate(searchParameter, modifier, comparator, searchValue);

return new SearchParameterPredicateExpression(
    searchParameter,
    modifier,
    comparator,
    componentIndex,
    searchValue);
```

6. For multiple values, parse, target-adjust, and **validate each part in input order before constructing the predicate**. This preserves the legacy exception precedence: an earlier value's modifier/comparator validation fails before a later value's parse error.

**:not modifier case:**
```csharp
if (modifier?.SearchModifierCode == SearchModifierCode.Not)
{
    // Each semantic predicate carries a null modifier so that lowering produces a single
    // outer Not rather than double negation. Parse then validate each part in input order
    // (matching legacy behavior) before parsing the next.
    Expression[] expressions = parts.Select(part =>
    {
        ISearchValue searchValue = parser(part);
        ValidateSemanticPredicate(searchParameter, modifier: null, comparator, searchValue);

        return (Expression)new SearchParameterPredicateExpression(
            searchParameter,
            modifier: null,
            comparator,
            componentIndex,
            searchValue);
    }).ToArray();

    return Expression.Not(Expression.Or(expressions));
}
```

**Non-:not multiple-value case:**
```csharp
// Parse, target-adjust, then validate each part in input order (matching legacy
// behavior) before parsing the next. This ensures an earlier value's
// modifier/comparator validation takes precedence over a later value's parse failure.
Expression[] expressions = parts.Select(part =>
{
    ISearchValue searchValue = parser(part);
    searchValue = ApplyTargetTypeModifier(modifier, searchValue);
    ValidateSemanticPredicate(searchParameter, modifier, comparator, searchValue);

    return (Expression)new SearchParameterPredicateExpression(
        searchParameter,
        modifier,
        comparator,
        componentIndex,
        searchValue);
}).ToArray();

return Expression.Or(expressions);
```

7. Keep the existing local `ApplyTargetTypeModifier` function unchanged.

8. Keep the final outer wrapper: `return Expression.SearchParameter(searchParameter, outputExpression);`

9. **Add the semantic validation method** (small, explicit boundary):

```csharp
/// <summary>
/// Validates that the modifier/comparator/value-type combination is supported, throwing the same
/// InvalidSearchOperationException messages the legacy mapping would produce. This is a small,
/// explicit semantic validation boundary so the parser does not depend on legacy lowering for
/// input-order exception precedence. LegacyExpressionLowerer remains defensively validating.
/// </summary>
private static void ValidateSemanticPredicate(
    SearchParameterInfo searchParameter,
    SearchModifier modifier,
    SearchComparator comparator,
    ISearchValue searchValue)
{
    switch (searchValue)
    {
        case DateTimeSearchValue:
        case NumberSearchValue:
        case QuantitySearchValue:
            if (modifier != null)
            {
                ThrowModifierNotSupported(modifier, searchParameter.Code);
            }
            break;

        case ReferenceSearchValue:
            if (modifier != null && modifier.SearchModifierCode != SearchModifierCode.Type)
            {
                ThrowModifierNotSupported(modifier, searchParameter.Code);
            }
            EnsureOnlyEqualComparatorIsSupported(comparator);
            break;

        case StringSearchValue:
            EnsureOnlyEqualComparatorIsSupported(comparator);
            if (modifier != null &&
                modifier.SearchModifierCode != SearchModifierCode.Exact &&
                modifier.SearchModifierCode != SearchModifierCode.Contains)
            {
                ThrowModifierNotSupported(modifier, searchParameter.Code);
            }
            break;

        case TokenSearchValue:
            EnsureOnlyEqualComparatorIsSupported(comparator);
            if (modifier != null && modifier.SearchModifierCode != SearchModifierCode.Not)
            {
                ThrowModifierNotSupported(modifier, searchParameter.Code);
            }
            break;

        case UriSearchValue:
            if (modifier != null &&
                modifier.SearchModifierCode != SearchModifierCode.Above &&
                modifier.SearchModifierCode != SearchModifierCode.Below)
            {
                ThrowModifierNotSupported(modifier, searchParameter.Code);
            }
            break;
    }
}

private static void EnsureOnlyEqualComparatorIsSupported(SearchComparator comparator)
{
    if (comparator != SearchComparator.Eq)
    {
        throw new InvalidSearchOperationException(Core.Resources.OnlyEqualComparatorIsSupported);
    }
}

private static void ThrowModifierNotSupported(SearchModifier modifier, string searchParameterName)
{
    throw new InvalidSearchOperationException(
        string.Format(CultureInfo.InvariantCulture, Core.Resources.ModifierNotSupported, modifier, searchParameterName));
}
```

Do not alter `:missing` validation, token `:text` validation, composite arity validation, reference target-type validation, or exception messages. For composites, the existing `Build`-to-`BuildSemantic` call-site change ensures each inner predicate receives the resolved component `SearchParameterInfo` and zero-based component index while the outer `SearchParameterExpression` remains unchanged.

- [ ] **Step 6: Add a regression test for input-order semantic validation**

Add this test to `SearchValueExpressionBuilderTests` to verify that modifier/comparator validation takes precedence over later parse failures:

```csharp
[Fact]
public void GivenNumberWithUnsupportedContainsModifier_WhenMultipleValuesIncludingMalformed_ThenValidationExceptionPrecedesParseException()
{
    // Arrange: Number parameter with unsupported :Contains modifier and multiple values
    // where the first is valid but the second is malformed. The modifier should fail
    // before the parser error on the second value.
    SearchParameterInfo parameter = CreateSearchParameter(SearchParamType.Number);
    var modifier = new SearchModifier(SearchModifierCode.Contains);

    // Act & Assert: the :Contains modifier on Number should fail during validation,
    // not during parsing of the second (malformed) value "bad".
    var ex = Assert.Throws<InvalidSearchOperationException>(() =>
        _parser.ParseSemantic(parameter, modifier, "1,bad"));

    Assert.Contains("Contains", ex.Message);  // Modifier validation error, not parse error
}

[Fact]
public void GivenNumberWithUnsupportedContainsModifier_WhenMultipleValuesIncludingMalformed_LegacyParsePropagatesValidationError()
{
    // Arrange: verify that the legacy Parse API also validates in input order
    SearchParameterInfo parameter = CreateSearchParameter(SearchParamType.Number);
    var modifier = new SearchModifier(SearchModifierCode.Contains);

    // Act & Assert: the modifier should fail, not the parse error on "bad"
    var ex = Assert.Throws<InvalidSearchOperationException>(() =>
        _parser.Parse(parameter, modifier, "1,bad"));

    Assert.Contains("Contains", ex.Message);
}
```

- [ ] **Step 7: Prove semantic-to-legacy equivalence for representative values**

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

- [ ] **Step 8: Run all existing search-value expression tests**

```powershell
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
```

Expected: all pre-existing tests and new semantic tests pass. Existing tests prove the legacy `Parse` output is unchanged.

- [ ] **Step 9: Run the same tests for every FHIR version**

```powershell
dotnet test "src\Microsoft.Health.Fhir.Stu3.Core.UnitTests\Microsoft.Health.Fhir.Stu3.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4.Core.UnitTests\Microsoft.Health.Fhir.R4.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R4B.Core.UnitTests\Microsoft.Health.Fhir.R4B.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
dotnet test "src\Microsoft.Health.Fhir.R5.Core.UnitTests\Microsoft.Health.Fhir.R5.Core.UnitTests.csproj" --framework net9.0 --filter "FullyQualifiedName~SearchValueExpressionBuilderTests" --no-restore --verbosity quiet
```

Expected: PASS for STU3, R4, R4B, and R5.

- [ ] **Step 10: Commit semantic-first parsing**

```powershell
git add "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\Parsers\ISearchParameterExpressionParser.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\Parsers\SearchParameterExpressionParser.cs" "src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\Expressions\Parsers\SearchValueExpressionBuilderTests.cs"
git commit -m "Parse search parameters into semantic expressions" -m "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>"
```

## Task 4: Document and verify the compatibility boundary

**Files:**
- Modify: `docs/SearchArchitecture.md`

- [ ] **Step 1: Document semantic-first parsing**

Update `docs/SearchArchitecture.md`. The table of contents should now list a new "Request compilation" section before "Extraction". The complete section (now present in the shipped file) should include:

```markdown
## Request compilation

When the FHIR service handles a search request, `ExpressionParser` parses each query parameter.
Ordinary typed search-parameter values become **semantic predicate** leaves
(`SearchParameterPredicateExpression`) before any backend-specific translation occurs.
Existing structural and specialized nodes remain in the expression tree alongside this new leaf
type: the `:missing` modifier is resolved immediately to `MissingSearchParameterExpression` and
does **not** become a `SearchParameterPredicateExpression`; it does not pass through
`LegacyExpressionLowerer`'s predicate-override path.

A semantic predicate retains everything the parser resolved:

| Property | Description |
|---|---|
| `Parameter` | The resolved `SearchParameterInfo` identity (name, type, URL). |
| `Comparator` | The FHIR comparator (`eq`, `gt`, `le`, …) applied to the query value. |
| `Modifier` | The optional search modifier where applicable (e.g. `:exact`, `:contains`, `:not`, `:text`, `:type`, `:above`, `:below`). |
| `ComponentIndex` | The zero-based composite-component position, or `null` for non-composite parameters. |
| `Value` | The normalized `ISearchValue` (e.g. `DateTimeSearchValue`, `TokenSearchValue`). |

Semantic predicates deliberately **do not** reference persisted field names or any backend schema.

### Strangler-migration compatibility

The current stage uses a *strangler-fig* approach so that all existing consumers remain unchanged.
`LegacyExpressionLowerer` immediately converts each `SearchParameterPredicateExpression` into the
existing `FieldName` / `BinaryExpression` / `StringExpression` representation that the public
`ExpressionParser` API has always returned. SQL Server, Cosmos DB, member-match, and all other
current consumers therefore retain identical behavior without modification.

The next migration stage will preserve the complete semantic expression tree on `SearchOptions`,
enabling a logical planning layer to reason over the full query before lowering to a backend dialect.
```

- [ ] **Step 2: Format changed C# files and check the diff**

```powershell
dotnet format "Microsoft.Health.Fhir.sln" --no-restore --include "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\SearchParameterPredicateExpression.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\LegacyExpressionLowerer.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\IExpressionVisitor.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\ExpressionRewriter.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\Parsers\ISearchParameterExpressionParser.cs" "src\Microsoft.Health.Fhir.Core\Features\Search\Expressions\Parsers\SearchParameterExpressionParser.cs" "src\Microsoft.Health.Fhir.Core.UnitTests\Features\Search\Expressions\LegacyExpressionLowererTests.cs" "src\Microsoft.Health.Fhir.Shared.Core.UnitTests\Features\Search\Expressions\Parsers\SearchValueExpressionBuilderTests.cs"
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

- `SearchParameterPredicateExpression` captures FHIR comparator, modifier, normalized value, SearchParameter identity, and optional composite position;
- `ParseSemantic` constructs semantic expressions with `SearchParameterPredicateExpression` leaves;
- `Parse` remains the same compatibility API and returns the same legacy expression shapes via `LegacyExpressionLowerer.Instance.Lower(ParseSemantic(...))`;
- `SearchValueExpressionBuilderHelper` is invoked only by `LegacyExpressionLowerer`;
- semantic validation (`ValidateSemanticPredicate`) is performed in input order during `ParseSemantic`, ensuring modifier/comparator validation precedes later value parse errors;
- :missing remains a `MissingSearchParameterExpression`, not a `SearchParameterPredicateExpression`;
- `ExpressionRewriter` preserves semantic leaves via identity override of `VisitSearchParameterPredicate`;
- `DefaultExpressionVisitor` does not override `VisitSearchParameterPredicate`; direct legacy visitors rely on the interface guard that throws `InvalidOperationException`;
- a regression test verifies that a minimal visitor without explicit semantic-leaf handling fails at runtime when a semantic predicate is dispatched;
- `LegacyExpressionLowerer` defensively validates the three token-text invariants (Token type, Eq comparator, TokenSearchValue with non-null Text);
- no raw query syntax model is added;
- SQL Server and Cosmos code remain unchanged;
- existing parser tests pass for STU3, R4, R4B, and R5;
- the solution builds for `net9.0` with warnings treated as errors.
