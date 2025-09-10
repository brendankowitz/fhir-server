// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors
{
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
                    stringExpr.StringOperator == StringOperator.Equals)
                {
                    HasExactIdSearch = true;
                }
            }

            return base.VisitSearchParameter(expression, context);
        }
    }
}
