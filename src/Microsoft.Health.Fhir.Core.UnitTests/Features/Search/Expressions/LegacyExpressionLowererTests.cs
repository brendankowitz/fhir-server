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
        // -----------------------------------------------------------------------
        // Test 1: Semantic date equality lowers to GreaterThanOrEqual / LessThanOrEqual
        // -----------------------------------------------------------------------

        /// <summary>
        /// A SearchParameterExpression wrapping a SearchParameterPredicateExpression with
        /// Eq and a partial-month DateTimeSearchValue should lower to a SearchParameterExpression
        /// whose inner expression is an And MultiaryExpression with two BinaryExpression leaves:
        ///   - GreaterThanOrEqual on FieldName.DateTimeStart
        ///   - LessThanOrEqual   on FieldName.DateTimeEnd
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

        // -----------------------------------------------------------------------
        // Test 2a (negative): :text with non-Eq comparator must throw
        // -----------------------------------------------------------------------

        /// <summary>
        /// A Text-modifier predicate on a Token parameter whose comparator is anything
        /// other than <see cref="SearchComparator.Eq"/> is a semantic invariant violation.
        /// The lowerer must reject it with <see cref="InvalidOperationException"/> rather
        /// than silently producing a StartsWith expression.
        /// The value is kept as a valid <see cref="TokenSearchValue"/> with non-null
        /// <see cref="TokenSearchValue.Text"/> so that only the comparator invariant is tested.
        /// </summary>
        [Fact]
        public void GivenTextModifierWithNonEqComparator_WhenLowered_ThrowsInvalidOperationException()
        {
            // Arrange
            var param = new SearchParameterInfo("code", "code", SearchParamType.Token);
            var tokenValue = new TokenSearchValue(system: null, code: null, text: "fever");
            var modifier = new SearchModifier(SearchModifierCode.Text);
            var predicate = new SearchParameterPredicateExpression(
                parameter: param,
                modifier: modifier,
                comparator: SearchComparator.Gt,   // violating invariant: only Eq is valid
                componentIndex: null,
                value: tokenValue);
            var wrapper = new SearchParameterExpression(param, predicate);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => LegacyExpressionLowerer.Instance.Lower(wrapper));
        }

        // -----------------------------------------------------------------------
        // Test 2b (negative): :text on a non-Token parameter type must throw
        // -----------------------------------------------------------------------

        /// <summary>
        /// A Text-modifier predicate whose parameter type is not Token is a semantic
        /// invariant violation. The lowerer must reject it with
        /// <see cref="InvalidOperationException"/> rather than silently producing a
        /// StartsWith expression.
        /// The value is kept as a valid <see cref="TokenSearchValue"/> with non-null
        /// <see cref="TokenSearchValue.Text"/> so that only the parameter-type invariant is tested.
        /// </summary>
        [Fact]
        public void GivenTextModifierOnNonTokenParameter_WhenLowered_ThrowsInvalidOperationException()
        {
            // Arrange — parameter type is String, not Token
            var param = new SearchParameterInfo("display", "display", SearchParamType.String);
            var tokenValue = new TokenSearchValue(system: null, code: null, text: "fever");
            var modifier = new SearchModifier(SearchModifierCode.Text);
            var predicate = new SearchParameterPredicateExpression(
                parameter: param,
                modifier: modifier,
                comparator: SearchComparator.Eq,
                componentIndex: null,
                value: tokenValue);
            var wrapper = new SearchParameterExpression(param, predicate);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => LegacyExpressionLowerer.Instance.Lower(wrapper));
        }

        // -----------------------------------------------------------------------
        // Test 2: Semantic token :text lowers to StartsWith on TokenText
        // -----------------------------------------------------------------------

        /// <summary>
        /// A SearchParameterExpression wrapping a SearchParameterPredicateExpression with
        /// modifier Text, Eq, and TokenSearchValue(system: null, code: null, text: @"heart\,lung")
        /// should lower to a SearchParameterExpression whose inner expression is a
        /// StringExpression StartsWith on FieldName.TokenText, exact raw value @"heart\,lung", ignoreCase true.
        /// The escaped value proves exact legacy compatibility.
        /// </summary>
        [Fact]
        public void GivenSemanticTokenTextPredicate_WhenLowered_ProducesStartsWithOnTokenText()
        {
            // Arrange
            const string rawText = @"heart\,lung";
            var param = new SearchParameterInfo("code", "code", SearchParamType.Token);
            var tokenValue = new TokenSearchValue(system: null, code: null, text: rawText);
            var modifier = new SearchModifier(SearchModifierCode.Text);
            var predicate = new SearchParameterPredicateExpression(
                parameter: param,
                modifier: modifier,
                comparator: SearchComparator.Eq,
                componentIndex: null,
                value: tokenValue);
            var wrapper = new SearchParameterExpression(param, predicate);

            // Act
            var result = LegacyExpressionLowerer.Instance.Lower(wrapper);

            // Assert – outer wrapper preserved
            var spe = Assert.IsType<SearchParameterExpression>(result);
            Assert.Same(param, spe.Parameter);

            // Inner expression must be a StringExpression StartsWith on TokenText
            var str = Assert.IsType<StringExpression>(spe.Expression);
            Assert.Equal(StringOperator.StartsWith, str.StringOperator);
            Assert.Equal(FieldName.TokenText, str.FieldName);
            Assert.Null(str.ComponentIndex);
            Assert.Equal(rawText, str.Value);
            Assert.True(str.IgnoreCase);
        }
    }
}
