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
    /// A one-way compatibility boundary that lowers semantic
    /// <see cref="SearchParameterPredicateExpression"/> leaves into the legacy
    /// field-level expression tree understood by the SQL and Cosmos backends.
    /// <para>
    /// All structural nodes (multiary, chained, not, sort, etc.) are preserved
    /// transparently via the inherited <see cref="ExpressionRewriter{TContext}"/> recursion.
    /// </para>
    /// <para>
    /// The only special case is the <c>:text</c> modifier on token parameters, because
    /// <see cref="SearchValueExpressionBuilderHelper"/> does not accept token-text semantics;
    /// it is handled inline here.
    /// </para>
    /// </summary>
    public sealed class LegacyExpressionLowerer : ExpressionRewriter<object>
    {
        /// <summary>
        /// Gets the singleton instance of <see cref="LegacyExpressionLowerer"/>.
        /// </summary>
        public static readonly LegacyExpressionLowerer Instance = new LegacyExpressionLowerer();

        private LegacyExpressionLowerer()
        {
        }

        /// <summary>
        /// Lowers all <see cref="SearchParameterPredicateExpression"/> nodes within
        /// <paramref name="expression"/> into legacy field-level expression nodes.
        /// </summary>
        /// <param name="expression">The expression tree to lower. Must not be <c>null</c>.</param>
        /// <returns>
        /// An equivalent expression tree containing only legacy field-level nodes;
        /// structural wrapper nodes are preserved unchanged.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="expression"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a <see cref="SearchParameterPredicateExpression"/> with the <c>:text</c> modifier
        /// violates an internal semantic invariant, specifically: the parameter type is not
        /// <see cref="SearchParamType.Token"/>, the comparator is not <see cref="SearchComparator.Eq"/>,
        /// or the value is not a <see cref="TokenSearchValue"/> with a non-null
        /// <see cref="TokenSearchValue.Text"/> property.
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
                        $"'{expression.Parameter.Code}' must carry a {nameof(TokenSearchValue)} with a non-null Text property.");
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
