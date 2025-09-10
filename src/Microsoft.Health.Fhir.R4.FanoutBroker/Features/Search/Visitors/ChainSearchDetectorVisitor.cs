// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Search.Expressions;

namespace Microsoft.Health.Fhir.FanoutBroker.Features.Search.Visitors
{
    /// <summary>
    /// Visitor to detect chained search expressions in the search expression tree.
    /// </summary>
    internal class ChainSearchDetectorVisitor : DefaultExpressionVisitor<object, object>
    {
        public bool HasChainedSearch { get; private set; }

        public override object VisitChained(ChainedExpression expression, object context)
        {
            HasChainedSearch = true;
            return null;
        }
    }
}
