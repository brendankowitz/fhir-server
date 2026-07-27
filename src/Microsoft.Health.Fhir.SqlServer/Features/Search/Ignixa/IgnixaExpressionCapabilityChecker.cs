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
    /// Decides whether a search expression may be executed via the Ignixa SQL path. Any expression referencing a
    /// user token or composite search parameter is currently kept on the legacy path. The <c>_type</c>
    /// resource-type restriction is a token parameter too, but it is a structural filter that Ignixa lowers
    /// natively onto the resource table rather than a token-table predicate, so it is explicitly allowed. This is
    /// a semantic check over the parsed FHIR expression tree rather than over emitted table names, so it catches
    /// every token predicate shape regardless of how the SQL generator later lowers it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This gate was introduced on the belief that the Ignixa compiler emits incorrect SQL for token-family
    /// parameters. That belief is <b>not currently substantiated</b>, and the evidence points the other way:
    /// </para>
    /// <list type="bullet">
    /// <item>Disabling this gate and running the full R4 integration suite against a live database produced no
    /// new SQL failures — only the two pre-existing intermittent flakes.</item>
    /// <item>Ignixa's own suite covers token lowering directly (single code, system|code, text, composite, and
    /// the 256-character overflow variants) and passes.</item>
    /// <item>Its legacy-SQL corpus, which diffs emitted SQL against SQL the shipping engine really executed, is
    /// heavy on token and <c>_tag</c>-scoped searches and reports no systematic token divergence.</item>
    /// </list>
    /// <para>
    /// The likely origin is a misread: a token search returning zero rows was attributed to Ignixa, but this
    /// fixture does not index every search parameter and the legacy engine returns zero for those same searches
    /// (<c>SqlServerIgnixaExecutionTests.GivenATokenSearch_WhenExecutedOnBothEngines_ThenIgnixaAgreesWithLegacy</c>
    /// covers exactly that case and asserts agreement rather than a row count).
    /// </para>
    /// <para>
    /// The gate is retained for now because removing it is a widening of the cutover that deserves its own
    /// verified step, not because the defect it names has been demonstrated. Whoever narrows these gates should
    /// either produce a failing test that justifies this one or delete it.
    /// </para>
    /// </remarks>
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
        /// Returns <see langword="true"/> when the parameter is a user token or composite predicate that is
        /// currently deferred to the legacy path. The <c>_type</c> resource-type restriction is a token parameter
        /// but is excluded because Ignixa lowers it natively onto the resource table rather than as a token-table
        /// predicate.
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
