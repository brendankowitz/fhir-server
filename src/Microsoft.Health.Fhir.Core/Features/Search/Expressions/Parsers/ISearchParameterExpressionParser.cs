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
        /// whose leaves are <see cref="SearchParameterPredicateExpression"/> nodes. The semantic tree
        /// preserves the resolved <see cref="SearchParameterInfo"/>, <see cref="SearchModifier"/>,
        /// comparator, component index, and normalized search value, and must be lowered (for example
        /// via <see cref="LegacyExpressionLowerer"/>) before it reaches a backend expression visitor.
        /// </summary>
        /// <param name="searchParameter">The search parameter being queried.</param>
        /// <param name="modifier">The optional search modifier applied to the parameter, or <c>null</c> if none.</param>
        /// <param name="value">The raw search value.</param>
        /// <returns>A semantic <see cref="Expression"/> tree with <see cref="SearchParameterPredicateExpression"/> leaves.</returns>
        Expression ParseSemantic(
            SearchParameterInfo searchParameter,
            SearchModifier modifier,
            string value);
    }
}
