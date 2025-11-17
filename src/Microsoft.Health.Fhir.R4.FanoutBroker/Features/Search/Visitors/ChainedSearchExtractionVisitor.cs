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
    /// Visitor that extracts chained search expressions from a search expression tree.
    /// This enables sophisticated processing of chained search parameters with access
    /// to rich metadata about reference relationships and target types.
    /// </summary>
    internal class ChainedSearchExtractionVisitor : DefaultExpressionVisitor<object, object>
    {
        private readonly List<ChainedExpression> _chainedExpressions = new();

        /// <summary>
        /// Gets all chained search expressions found in the search expression.
        /// </summary>
        public IReadOnlyList<ChainedExpression> ChainedExpressions => _chainedExpressions;

        /// <summary>
        /// Extracts chained search expressions from the given search expression.
        /// </summary>
        /// <param name="expression">The search expression to process.</param>
        /// <returns>This visitor instance for method chaining.</returns>
        public ChainedSearchExtractionVisitor Extract(Expression expression)
        {
            _chainedExpressions.Clear();
            expression?.AcceptVisitor(this, null);
            return this;
        }

        /// <summary>
        /// Visits chained expressions and collects them for processing.
        /// </summary>
        public override object VisitChained(ChainedExpression expression, object context)
        {
            _chainedExpressions.Add(expression);

            // Continue visiting nested expressions
            return base.VisitChained(expression, context);
        }

        /// <summary>
        /// Determines if the search expression contains any chained search expressions.
        /// </summary>
        /// <param name="expression">The search expression to check.</param>
        /// <returns>True if chained searches are present, false otherwise.</returns>
        public static bool HasChainedSearches(Expression expression)
        {
            var visitor = new ChainedSearchExtractionVisitor();
            visitor.Extract(expression);
            return visitor.ChainedExpressions.Any();
        }

        /// <summary>
        /// Counts the maximum chain depth in the search expression.
        /// </summary>
        /// <param name="expression">The search expression to analyze.</param>
        /// <returns>The maximum chain depth found.</returns>
        public static int GetMaxChainDepth(Expression expression)
        {
            var visitor = new ChainedSearchExtractionVisitor();
            visitor.Extract(expression);

            if (!visitor.ChainedExpressions.Any())
                return 0;

            return visitor.ChainedExpressions.Max(CalculateChainDepth);
        }

        /// <summary>
        /// Calculates the depth of a chained expression.
        /// </summary>
        /// <param name="chainedExpression">The chained expression to analyze.</param>
        /// <returns>The depth of the chain.</returns>
        private static int CalculateChainDepth(ChainedExpression chainedExpression)
        {
            int depth = 1;
            var current = chainedExpression.Expression;

            while (current is ChainedExpression nested)
            {
                depth++;
                current = nested.Expression;
            }

            return depth;
        }

        /// <summary>
        /// Gets all unique target resource types referenced in chained searches.
        /// </summary>
        /// <param name="expression">The search expression to analyze.</param>
        /// <returns>Set of target resource types.</returns>
        public static HashSet<string> GetChainedTargetResourceTypes(Expression expression)
        {
            var visitor = new ChainedSearchExtractionVisitor();
            visitor.Extract(expression);

            var targetTypes = new HashSet<string>();

            foreach (var chainExpr in visitor.ChainedExpressions)
            {
                if (chainExpr.ReferenceSearchParameter?.TargetResourceTypes != null)
                {
                    foreach (var targetType in chainExpr.ReferenceSearchParameter.TargetResourceTypes)
                    {
                        targetTypes.Add(targetType);
                    }
                }
            }

            return targetTypes;
        }
    }
}