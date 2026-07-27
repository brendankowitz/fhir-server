// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa
{
    /// <summary>
    /// Decides whether a search expression may be executed via the Ignixa SQL path. The current
    /// Ignixa compiler emits incorrect SQL for token-family search parameters (single code,
    /// system|code, and 256+ character overflow variants), so any expression that references a
    /// user token or composite search parameter is kept on the legacy path until that capability gap
    /// is closed. The <c>_type</c> resource-type restriction is a token parameter too, but it is a
    /// structural filter that Ignixa lowers natively onto the resource table rather than a token-table
    /// predicate, so it is explicitly allowed. This is a semantic check over the parsed FHIR expression
    /// tree rather than a check over emitted table names, so it catches every token predicate shape
    /// regardless of how the SQL generator later lowers it.
    /// </summary>
    internal static class IgnixaExpressionCapabilityChecker
    {
        /// <summary>
        /// Returns <see langword="true"/> when the supplied expression contains no token or composite
        /// search parameter and is therefore safe to route to the Ignixa execution path.
        /// </summary>
        /// <param name="expression">The parsed FHIR search expression, or <see langword="null"/> for a match-all search.</param>
        /// <returns><see langword="true"/> when Ignixa may execute the query; otherwise <see langword="false"/>.</returns>
        public static bool IsSupported(Expression expression)
        {
            if (expression == null)
            {
                return true;
            }

            return !expression.AcceptVisitor(TokenPredicateDetector.Instance, context: null);
        }

        /// <summary>
        /// Returns <see langword="true"/> when the parameter is a user token or composite predicate that the
        /// Ignixa compiler cannot yet emit correctly, and must therefore stay on the legacy path. The
        /// <c>_type</c> resource-type restriction is a token parameter but is excluded because Ignixa lowers it
        /// natively onto the resource table rather than as a token-table predicate.
        /// </summary>
        private static bool IsDeferredTokenParameter(SearchParameterInfo parameter)
        {
            if (parameter == null)
            {
                return false;
            }

            if (string.Equals(parameter.Code, SearchParameterNames.ResourceType, StringComparison.Ordinal))
            {
                return false;
            }

            return parameter.Type == SearchParamType.Token || parameter.Type == SearchParamType.Composite;
        }

        private sealed class TokenPredicateDetector : DefaultExpressionVisitor<object, bool>
        {
            public static readonly TokenPredicateDetector Instance = new TokenPredicateDetector();

            private TokenPredicateDetector()
                : base((accumulated, current) => accumulated || current)
            {
            }

            public override bool VisitSearchParameter(SearchParameterExpression expression, object context)
            {
                if (IsDeferredTokenParameter(expression?.Parameter))
                {
                    return true;
                }

                return expression.Expression.AcceptVisitor(this, context);
            }

            public override bool VisitMissingSearchParameter(MissingSearchParameterExpression expression, object context)
            {
                return IsDeferredTokenParameter(expression?.Parameter);
            }
        }
    }
}
