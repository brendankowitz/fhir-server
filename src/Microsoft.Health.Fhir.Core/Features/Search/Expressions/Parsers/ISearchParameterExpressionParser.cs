// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers
{
    public interface ISearchParameterExpressionParser
    {
        /// <summary>
        /// Parses the specified search parameter, modifier, and value into a legacy field-level
        /// expression tree understood by the SQL and Cosmos backends.
        /// </summary>
        /// <param name="searchParameter">The search parameter being queried.</param>
        /// <param name="modifier">The optional search modifier applied to the parameter, or <c>null</c> if none.</param>
        /// <param name="value">The raw search value.</param>
        /// <returns>A legacy field-level <see cref="Expression"/> tree.</returns>
        Expression Parse(
            SearchParameterInfo searchParameter,
            SearchModifier modifier,
            string value);

        /// <summary>
        /// Parses the specified search parameter, modifier, and value into a semantic expression tree
        /// with <see cref="SearchParameterPredicateExpression"/> leaves for ordinary typed values,
        /// alongside specialized/structural nodes for modifiers represented structurally (e.g.,
        /// <see cref="MissingSearchParameterExpression"/> for `:missing`, <see cref="NotExpression"/>
        /// for multi-value `:not`). Preserved semantics include the resolved <see cref="SearchParameterInfo"/>,
        /// comparator, and value (ordinarily normalized; token `:text` retains raw escaped input).
        /// Modifier semantics are preserved either on predicate leaves or through structural nodes.
        /// The expression must be lowered (for example via <see cref="LegacyExpressionLowerer"/>)
        /// before it reaches a backend expression visitor.
        /// </summary>
        /// <param name="searchParameter">The search parameter being queried.</param>
        /// <param name="modifier">The optional search modifier applied to the parameter, or <c>null</c> if none.</param>
        /// <param name="value">The raw search value.</param>
        /// <returns>A semantic <see cref="Expression"/> tree with <see cref="SearchParameterPredicateExpression"/> leaves for ordinary values, alongside specialized nodes for structural semantics.</returns>
        Expression ParseSemantic(
            SearchParameterInfo searchParameter,
            SearchModifier modifier,
            string value);
    }
}
