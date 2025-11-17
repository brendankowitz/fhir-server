// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors
{
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
                    stringExpr.StringOperator == StringOperator.Equals &&
                    stringExpr.Value?.Contains('|', System.StringComparison.Ordinal) == true)
                {
                    HasSpecificIdentifier = true;
                }
            }

            return base.VisitSearchParameter(expression, context);
        }
    }
}
