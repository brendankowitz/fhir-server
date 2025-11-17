// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors
{
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
