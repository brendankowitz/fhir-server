// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using FhirExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;
using IgnixaExpression = Ignixa.Search.Expressions.Expression;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Provides a one-way compatibility bridge that converts an Ignixa legacy (lowered) expression tree into the
    /// FHIR Server legacy <see cref="FhirExpression"/> model consumed by the Cosmos DB query builder.
    /// </summary>
    /// <remarks>
    /// The bridge does not re-parse values with the FHIR Server legacy parser and does not introduce a new semantic
    /// model. It performs a structural, node-by-node translation of the already-lowered Ignixa expression. Nodes or
    /// values that cannot be faithfully represented in the FHIR Server model fail before the query reaches Cosmos DB.
    /// </remarks>
    public interface IIgnixaLegacyExpressionBridge
    {
        /// <summary>
        /// Converts an Ignixa legacy (lowered) expression into the equivalent FHIR Server expression tree.
        /// </summary>
        /// <param name="loweredExpression">
        /// The Ignixa expression that has already been lowered with
        /// <see cref="Ignixa.Search.Expressions.LegacyExpressionLowerer"/>.
        /// </param>
        /// <returns>The equivalent FHIR Server <see cref="FhirExpression"/> tree.</returns>
        FhirExpression Convert(IgnixaExpression loweredExpression);
    }
}
