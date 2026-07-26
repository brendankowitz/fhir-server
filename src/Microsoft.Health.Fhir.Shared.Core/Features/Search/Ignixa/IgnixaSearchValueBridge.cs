// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using FhirBinaryOperator = Microsoft.Health.Fhir.Core.Features.Search.Expressions.BinaryOperator;
using FhirFieldName = Microsoft.Health.Fhir.Core.Features.Search.Expressions.FieldName;
using FhirMultiaryOperator = Microsoft.Health.Fhir.Core.Features.Search.Expressions.MultiaryOperator;
using FhirStringOperator = Microsoft.Health.Fhir.Core.Features.Search.Expressions.StringOperator;
using FhirUnionOperator = Microsoft.Health.Fhir.Core.Features.Search.Expressions.UnionOperator;
using IgnixaBinaryOperator = Ignixa.Search.Expressions.BinaryOperator;
using IgnixaFieldName = Ignixa.Search.Expressions.FieldName;
using IgnixaMultiaryOperator = Ignixa.Search.Expressions.MultiaryOperator;
using IgnixaStringOperator = Ignixa.Search.Expressions.StringOperator;
using IgnixaUnionOperator = Ignixa.Search.Expressions.UnionOperator;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Converts the discrete field names, operators, and lowered field values of an Ignixa legacy expression into the
    /// equivalent FHIR Server search primitives consumed by the Cosmos DB query builder.
    /// </summary>
    /// <remarks>
    /// After <see cref="Ignixa.Search.Expressions.LegacyExpressionLowerer.LowerToLegacy(Ignixa.Search.Expressions.Expression)"/>
    /// runs, the higher level Ignixa search values (token, quantity, reference, date-time, number, string, URI, and
    /// composite) have already been decomposed into discrete field level nodes carrying primitive .NET operands
    /// (for example <see cref="decimal"/>, <see cref="DateTimeOffset"/>, and <see cref="string"/>). This helper
    /// performs a faithful, type-checked translation of those primitives without re-parsing any value text.
    /// </remarks>
    internal static class IgnixaSearchValueBridge
    {
        /// <summary>
        /// Maps an Ignixa <see cref="IgnixaFieldName"/> to the FHIR Server <see cref="FhirFieldName"/>.
        /// </summary>
        /// <param name="fieldName">The Ignixa field name.</param>
        /// <param name="parameterCode">The originating search parameter code, used for error reporting.</param>
        /// <returns>The equivalent FHIR Server field name.</returns>
        /// <exception cref="IgnixaExpressionBridgeException">
        /// Thrown when the Ignixa field name has no FHIR Server equivalent that the Cosmos DB pipeline can execute.
        /// </exception>
        public static FhirFieldName ConvertFieldName(IgnixaFieldName fieldName, string parameterCode)
        {
            return fieldName switch
            {
                IgnixaFieldName.DateTimeStart => FhirFieldName.DateTimeStart,
                IgnixaFieldName.DateTimeEnd => FhirFieldName.DateTimeEnd,

                // Ignixa names the two bounds of a numeric or quantity range separately because SQL Server
                // stores them as separate low/high columns, and the FHIR prefix table maps gt to the high
                // bound but sa to the low bound — so one field plus an operator cannot say which column is
                // meant. Cosmos has no such split: it indexes a single scalar per value ("n"/"q"), and its
                // LowNumberName/HighNumberName/LowQuantityName/HighQuantityName constants are unused
                // throughout src. Collapsing both bounds onto the one FHIR Server field is therefore
                // faithful here rather than lossy — against a point-valued index the operator alone carries
                // the full comparison semantics, which is exactly what the legacy Cosmos path already did.
                IgnixaFieldName.NumberLow or IgnixaFieldName.NumberHigh => FhirFieldName.Number,
                IgnixaFieldName.ParamName => FhirFieldName.ParamName,
                IgnixaFieldName.QuantityCode => FhirFieldName.QuantityCode,
                IgnixaFieldName.QuantitySystem => FhirFieldName.QuantitySystem,
                IgnixaFieldName.QuantityLow or IgnixaFieldName.QuantityHigh => FhirFieldName.Quantity,
                IgnixaFieldName.ReferenceBaseUri => FhirFieldName.ReferenceBaseUri,
                IgnixaFieldName.ReferenceResourceType => FhirFieldName.ReferenceResourceType,
                IgnixaFieldName.ReferenceResourceId => FhirFieldName.ReferenceResourceId,
                IgnixaFieldName.String => FhirFieldName.String,
                IgnixaFieldName.TokenCode => FhirFieldName.TokenCode,
                IgnixaFieldName.TokenSystem => FhirFieldName.TokenSystem,
                IgnixaFieldName.TokenText => FhirFieldName.TokenText,
                IgnixaFieldName.Uri => FhirFieldName.Uri,

                // The following Ignixa field names (URI version/fragment and identifier type system/code) have no
                // FHIR Server field-name equivalent. The FHIR Server Cosmos pipeline cannot index or query them, so
                // fail explicitly rather than silently dropping or broadening the predicate.
                _ => throw new IgnixaExpressionBridgeException(
                    nameof(Ignixa.Search.Expressions.IFieldExpression),
                    parameterCode,
                    $"Ignixa field '{fieldName}' has no FHIR Server field-name equivalent supported by Cosmos."),
            };
        }

        /// <summary>
        /// Maps an Ignixa <see cref="IgnixaBinaryOperator"/> to the FHIR Server <see cref="FhirBinaryOperator"/>.
        /// </summary>
        /// <param name="binaryOperator">The Ignixa binary operator.</param>
        /// <param name="parameterCode">The originating search parameter code, used for error reporting.</param>
        /// <returns>The equivalent FHIR Server binary operator.</returns>
        /// <exception cref="IgnixaExpressionBridgeException">Thrown when the operator has no FHIR Server equivalent.</exception>
        public static FhirBinaryOperator ConvertBinaryOperator(IgnixaBinaryOperator binaryOperator, string parameterCode)
        {
            return binaryOperator switch
            {
                IgnixaBinaryOperator.Equal => FhirBinaryOperator.Equal,
                IgnixaBinaryOperator.GreaterThan => FhirBinaryOperator.GreaterThan,
                IgnixaBinaryOperator.GreaterThanOrEqual => FhirBinaryOperator.GreaterThanOrEqual,
                IgnixaBinaryOperator.LessThan => FhirBinaryOperator.LessThan,
                IgnixaBinaryOperator.LessThanOrEqual => FhirBinaryOperator.LessThanOrEqual,
                IgnixaBinaryOperator.NotEqual => FhirBinaryOperator.NotEqual,
                _ => throw new IgnixaExpressionBridgeException(
                    nameof(Ignixa.Search.Expressions.BinaryExpression),
                    parameterCode,
                    $"Ignixa binary operator '{binaryOperator}' has no FHIR Server equivalent."),
            };
        }

        /// <summary>
        /// Maps an Ignixa <see cref="IgnixaStringOperator"/> to the FHIR Server <see cref="FhirStringOperator"/>.
        /// </summary>
        /// <param name="stringOperator">The Ignixa string operator.</param>
        /// <param name="parameterCode">The originating search parameter code, used for error reporting.</param>
        /// <returns>The equivalent FHIR Server string operator.</returns>
        /// <exception cref="IgnixaExpressionBridgeException">Thrown when the operator has no FHIR Server equivalent.</exception>
        public static FhirStringOperator ConvertStringOperator(IgnixaStringOperator stringOperator, string parameterCode)
        {
            return stringOperator switch
            {
                IgnixaStringOperator.Contains => FhirStringOperator.Contains,
                IgnixaStringOperator.EndsWith => FhirStringOperator.EndsWith,
                IgnixaStringOperator.Equals => FhirStringOperator.Equals,
                IgnixaStringOperator.NotContains => FhirStringOperator.NotContains,
                IgnixaStringOperator.NotEndsWith => FhirStringOperator.NotEndsWith,
                IgnixaStringOperator.NotStartsWith => FhirStringOperator.NotStartsWith,
                IgnixaStringOperator.StartsWith => FhirStringOperator.StartsWith,
                IgnixaStringOperator.LeftSideStartsWith => FhirStringOperator.LeftSideStartsWith,
                _ => throw new IgnixaExpressionBridgeException(
                    nameof(Ignixa.Search.Expressions.StringExpression),
                    parameterCode,
                    $"Ignixa string operator '{stringOperator}' has no FHIR Server equivalent."),
            };
        }

        /// <summary>
        /// Maps an Ignixa <see cref="IgnixaMultiaryOperator"/> to the FHIR Server <see cref="FhirMultiaryOperator"/>.
        /// </summary>
        /// <param name="multiaryOperator">The Ignixa multiary operator.</param>
        /// <param name="parameterCode">The originating search parameter code, used for error reporting.</param>
        /// <returns>The equivalent FHIR Server multiary operator.</returns>
        /// <exception cref="IgnixaExpressionBridgeException">Thrown when the operator has no FHIR Server equivalent.</exception>
        public static FhirMultiaryOperator ConvertMultiaryOperator(IgnixaMultiaryOperator multiaryOperator, string parameterCode)
        {
            return multiaryOperator switch
            {
                IgnixaMultiaryOperator.And => FhirMultiaryOperator.And,
                IgnixaMultiaryOperator.Or => FhirMultiaryOperator.Or,
                _ => throw new IgnixaExpressionBridgeException(
                    nameof(Ignixa.Search.Expressions.MultiaryExpression),
                    parameterCode,
                    $"Ignixa multiary operator '{multiaryOperator}' has no FHIR Server equivalent."),
            };
        }

        /// <summary>
        /// Maps an Ignixa <see cref="IgnixaUnionOperator"/> to the FHIR Server <see cref="FhirUnionOperator"/>.
        /// </summary>
        /// <param name="unionOperator">The Ignixa union operator.</param>
        /// <param name="parameterCode">The originating search parameter code, used for error reporting.</param>
        /// <returns>The equivalent FHIR Server union operator.</returns>
        /// <exception cref="IgnixaExpressionBridgeException">Thrown when the operator has no FHIR Server equivalent.</exception>
        public static FhirUnionOperator ConvertUnionOperator(IgnixaUnionOperator unionOperator, string parameterCode)
        {
            return unionOperator switch
            {
                IgnixaUnionOperator.All => FhirUnionOperator.All,
                _ => throw new IgnixaExpressionBridgeException(
                    nameof(Ignixa.Search.Expressions.UnionExpression),
                    parameterCode,
                    $"Ignixa union operator '{unionOperator}' has no FHIR Server equivalent supported by Cosmos."),
            };
        }

        /// <summary>
        /// Validates and normalizes a lowered Ignixa binary field value into the primitive operand consumed by the
        /// FHIR Server Cosmos query builder. The value is preserved exactly (including comparator bounds already
        /// encoded by the lowering step); only its runtime type is verified.
        /// </summary>
        /// <param name="value">The lowered Ignixa binary operand.</param>
        /// <param name="parameterCode">The originating search parameter code, used for error reporting.</param>
        /// <returns>The validated primitive operand.</returns>
        /// <exception cref="IgnixaExpressionBridgeException">Thrown when the operand type cannot be represented.</exception>
        public static object ConvertBinaryValue(object value, string parameterCode)
        {
            return value switch
            {
                null => throw new IgnixaExpressionBridgeException(
                    nameof(Ignixa.Search.Expressions.BinaryExpression),
                    parameterCode,
                    "Ignixa binary expression value cannot be null."),
                string s => s,
                decimal d => d,
                DateTimeOffset dto => dto,
                DateTime dt => dt,
                bool b => b,
                int i => i,
                long l => l,
                double db => db,
                _ => throw new IgnixaExpressionBridgeException(
                    nameof(Ignixa.Search.Expressions.BinaryExpression),
                    parameterCode,
                    $"Ignixa binary expression value of type '{value.GetType().FullName}' is not a supported Cosmos operand."),
            };
        }
    }
}
