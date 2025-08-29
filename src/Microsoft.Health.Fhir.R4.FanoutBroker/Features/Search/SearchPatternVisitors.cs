// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search
{
    /// <summary>
    /// Visitor to detect chained search expressions in the search expression tree.
    /// </summary>
    internal class ChainSearchDetectorVisitor : DefaultExpressionVisitor<object, object>
    {
        public bool HasChainedSearch { get; private set; }

        public override object VisitChainedExpression(ChainedExpression expression, object context)
        {
            HasChainedSearch = true;
            return null;
        }
    }

    /// <summary>
    /// Visitor to detect exact ID searches in the search expression tree.
    /// </summary>
    internal class IdSearchDetectorVisitor : DefaultExpressionVisitor<object, object>
    {
        public bool HasExactIdSearch { get; private set; }

        public override object VisitSearchParameter(SearchParameterExpression expression, object context)
        {
            if (expression.Parameter.Name.Equals("_id", System.StringComparison.OrdinalIgnoreCase))
            {
                // Check if it's an exact match (equals operation)
                if (expression.Expression is StringExpression stringExpr && 
                    stringExpr.StringOperator == StringOperator.Equal)
                {
                    HasExactIdSearch = true;
                }
            }

            return base.VisitSearchParameter(expression, context);
        }
    }

    /// <summary>
    /// Visitor to detect specific identifier searches with system|value format.
    /// </summary>
    internal class IdentifierSearchDetectorVisitor : DefaultExpressionVisitor<object, object>
    {
        public bool HasSpecificIdentifier { get; private set; }

        public override object VisitSearchParameter(SearchParameterExpression expression, object context)
        {
            if (expression.Parameter.Name.Equals("identifier", System.StringComparison.OrdinalIgnoreCase))
            {
                // Check if it's a specific identifier with system|value format
                if (expression.Expression is StringExpression stringExpr && 
                    stringExpr.StringOperator == StringOperator.Equal &&
                    stringExpr.Value?.Contains("|") == true)
                {
                    HasSpecificIdentifier = true;
                }
            }

            return base.VisitSearchParameter(expression, context);
        }
    }

    /// <summary>
    /// Visitor to detect broad text searches that are likely to return many results.
    /// </summary>
    internal class TextSearchDetectorVisitor : DefaultExpressionVisitor<object, object>
    {
        public bool HasBroadTextSearch { get; private set; }

        public override object VisitSearchParameter(SearchParameterExpression expression, object context)
        {
            // Check for common text search parameters
            var parameterName = expression.Parameter.Name.ToLowerInvariant();
            if (parameterName == "name" || parameterName == "family" || parameterName == "given")
            {
                if (expression.Expression is StringExpression stringExpr &&
                    (stringExpr.StringOperator == StringOperator.Contains ||
                     stringExpr.StringOperator == StringOperator.StartsWith))
                {
                    HasBroadTextSearch = true;
                }
            }

            return base.VisitSearchParameter(expression, context);
        }
    }

    /// <summary>
    /// Visitor to detect status-based searches that typically return many results.
    /// </summary>
    internal class StatusSearchDetectorVisitor : DefaultExpressionVisitor<object, object>
    {
        public bool HasStatusSearch { get; private set; }

        public override object VisitSearchParameter(SearchParameterExpression expression, object context)
        {
            var parameterName = expression.Parameter.Name.ToLowerInvariant();
            if (parameterName == "status" || parameterName == "category" || parameterName == "class")
            {
                HasStatusSearch = true;
            }

            return base.VisitSearchParameter(expression, context);
        }
    }
}