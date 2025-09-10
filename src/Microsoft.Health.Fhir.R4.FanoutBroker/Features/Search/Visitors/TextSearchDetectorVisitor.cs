// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors
{
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
}
