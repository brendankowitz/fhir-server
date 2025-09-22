// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors
{
    /// <summary>
    /// Visitor that extracts include expressions from a search expression tree.
    /// This enables sophisticated processing of _include and _revinclude parameters
    /// with support for iterative, wildcard, and reverse includes.
    /// </summary>
    internal class IncludeExtractionVisitor : DefaultExpressionVisitor<object, object>
    {
        private readonly List<IncludeExpression> _includeExpressions = new();
        private readonly List<IncludeExpression> _revIncludeExpressions = new();

        /// <summary>
        /// Gets all include expressions found in the search expression.
        /// </summary>
        public IReadOnlyList<IncludeExpression> IncludeExpressions => _includeExpressions;

        /// <summary>
        /// Gets all reverse include expressions found in the search expression.
        /// </summary>
        public IReadOnlyList<IncludeExpression> RevIncludeExpressions => _revIncludeExpressions;

        /// <summary>
        /// Gets all include expressions (both regular and reverse).
        /// </summary>
        public IReadOnlyList<IncludeExpression> AllIncludeExpressions =>
            _includeExpressions.Concat(_revIncludeExpressions).ToList();

        /// <summary>
        /// Extracts include expressions from the given search expression.
        /// </summary>
        /// <param name="expression">The search expression to process.</param>
        /// <returns>This visitor instance for method chaining.</returns>
        public IncludeExtractionVisitor Extract(Expression expression)
        {
            _includeExpressions.Clear();
            _revIncludeExpressions.Clear();
            expression?.AcceptVisitor(this, null);
            return this;
        }

        /// <summary>
        /// Visits include expressions and categorizes them by type.
        /// </summary>
        public override object VisitInclude(IncludeExpression expression, object context)
        {
            if (expression.Reversed)
            {
                _revIncludeExpressions.Add(expression);
            }
            else
            {
                _includeExpressions.Add(expression);
            }

            return null;
        }

        /// <summary>
        /// Determines if the search expression contains any include expressions.
        /// </summary>
        /// <param name="expression">The search expression to check.</param>
        /// <returns>True if includes are present, false otherwise.</returns>
        public static bool HasIncludes(Expression expression)
        {
            var visitor = new IncludeExtractionVisitor();
            visitor.Extract(expression);
            return visitor.AllIncludeExpressions.Any();
        }

        /// <summary>
        /// Determines if the search expression contains iterative includes.
        /// </summary>
        /// <param name="expression">The search expression to check.</param>
        /// <returns>True if iterative includes are present, false otherwise.</returns>
        public static bool HasIterativeIncludes(Expression expression)
        {
            var visitor = new IncludeExtractionVisitor();
            visitor.Extract(expression);
            return visitor.AllIncludeExpressions.Any(inc => inc.Iterate);
        }

        /// <summary>
        /// Determines if the search expression contains wildcard includes.
        /// </summary>
        /// <param name="expression">The search expression to check.</param>
        /// <returns>True if wildcard includes are present, false otherwise.</returns>
        public static bool HasWildcardIncludes(Expression expression)
        {
            var visitor = new IncludeExtractionVisitor();
            visitor.Extract(expression);
            return visitor.AllIncludeExpressions.Any(inc => inc.WildCard);
        }

        /// <summary>
        /// Determines if the search expression contains circular reference includes.
        /// </summary>
        /// <param name="expression">The search expression to check.</param>
        /// <returns>True if circular reference includes are present, false otherwise.</returns>
        public static bool HasCircularReferenceIncludes(Expression expression)
        {
            var visitor = new IncludeExtractionVisitor();
            visitor.Extract(expression);
            return visitor.AllIncludeExpressions.Any(inc => inc.CircularReference);
        }
    }
}