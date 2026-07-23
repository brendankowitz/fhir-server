// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Definition;
using FhirExpr = Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using FhirSpi = Microsoft.Health.Fhir.Core.Models.SearchParameterInfo;
using IgnixaExpr = Ignixa.Search.Expressions;
using IgnixaSpi = Ignixa.Search.Models.SearchParameterInfo;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Performs an exhaustive, structural translation of an Ignixa legacy (lowered) expression tree into the FHIR
    /// Server <see cref="FhirExpr.Expression"/> model. The visitor preserves Boolean shape, operand ordering, chain
    /// direction and target types, include mode and iterate state, missing/not semantics, and composite component
    /// ordering without applying any semantic transformation such as De Morgan rewrites.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The context parameter carries the search parameter code of the enclosing
    /// <see cref="IgnixaExpr.SearchParameterExpression"/>, so field level nodes can include the originating parameter
    /// code in failure metadata.
    /// </para>
    /// <para>
    /// FHIR Server search parameter metadata is resolved from the canonical
    /// <see cref="ISearchParameterDefinitionManager"/> (by definition URL) rather than reconstructed, so the bridged
    /// tree carries the same <see cref="FhirSpi"/> instances the legacy parser would have produced.
    /// </para>
    /// </remarks>
    internal sealed class IgnixaLegacyExpressionBridgeVisitor : IgnixaExpr.IExpressionVisitor<string, FhirExpr.Expression>
    {
        private readonly ISearchParameterDefinitionManager _searchParameterDefinitionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="IgnixaLegacyExpressionBridgeVisitor"/> class.
        /// </summary>
        /// <param name="searchParameterDefinitionManager">The FHIR Server search parameter definition manager.</param>
        public IgnixaLegacyExpressionBridgeVisitor(ISearchParameterDefinitionManager searchParameterDefinitionManager)
        {
            EnsureArg.IsNotNull(searchParameterDefinitionManager, nameof(searchParameterDefinitionManager));

            _searchParameterDefinitionManager = searchParameterDefinitionManager;
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitSearchParameter(IgnixaExpr.SearchParameterExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            FhirSpi parameter = ResolveParameter(nameof(IgnixaExpr.SearchParameterExpression), expression.Parameter);
            FhirExpr.Expression inner = Convert(expression.Expression, parameter.Code);
            return new FhirExpr.SearchParameterExpression(parameter, inner);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitMissingSearchParameter(IgnixaExpr.MissingSearchParameterExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            FhirSpi parameter = ResolveParameter(nameof(IgnixaExpr.MissingSearchParameterExpression), expression.Parameter);
            return new FhirExpr.MissingSearchParameterExpression(parameter, expression.IsMissing);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitBinary(IgnixaExpr.BinaryExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            return new FhirExpr.BinaryExpression(
                IgnixaSearchValueBridge.ConvertBinaryOperator(expression.BinaryOperator, context),
                IgnixaSearchValueBridge.ConvertFieldName(expression.FieldName, context),
                expression.ComponentIndex,
                IgnixaSearchValueBridge.ConvertBinaryValue(expression.Value, context));
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitString(IgnixaExpr.StringExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            return new FhirExpr.StringExpression(
                IgnixaSearchValueBridge.ConvertStringOperator(expression.StringOperator, context),
                IgnixaSearchValueBridge.ConvertFieldName(expression.FieldName, context),
                expression.ComponentIndex,
                expression.Value,
                expression.IgnoreCase);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitMissingField(IgnixaExpr.MissingFieldExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            return new FhirExpr.MissingFieldExpression(
                IgnixaSearchValueBridge.ConvertFieldName(expression.FieldName, context),
                expression.ComponentIndex);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitIn<T>(IgnixaExpr.InExpression<T> expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            return new FhirExpr.InExpression<T>(
                IgnixaSearchValueBridge.ConvertFieldName(expression.FieldName, context),
                expression.ComponentIndex,
                expression.Values);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitNotExpression(IgnixaExpr.NotExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            return new FhirExpr.NotExpression(Convert(expression.Expression, context));
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitMultiary(IgnixaExpr.MultiaryExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            FhirExpr.Expression[] operands = ConvertOperands(expression.Expressions, context);
            return new FhirExpr.MultiaryExpression(
                IgnixaSearchValueBridge.ConvertMultiaryOperator(expression.MultiaryOperation, context),
                operands);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitUnion(IgnixaExpr.UnionExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            FhirExpr.Expression[] operands = ConvertOperands(expression.Expressions, context);
            return new FhirExpr.UnionExpression(
                IgnixaSearchValueBridge.ConvertUnionOperator(expression.Operator, context),
                operands);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitChained(IgnixaExpr.ChainedExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            FhirSpi referenceSearchParameter = ResolveParameter(nameof(IgnixaExpr.ChainedExpression), expression.ReferenceSearchParameter);
            FhirExpr.Expression inner = Convert(expression.Expression, context);

            return new FhirExpr.ChainedExpression(
                expression.ResourceTypes,
                referenceSearchParameter,
                expression.TargetResourceTypes,
                expression.Reversed,
                inner);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitInclude(IgnixaExpr.IncludeExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            FhirSpi referenceSearchParameter = expression.WildCard
                ? null
                : ResolveParameter(nameof(IgnixaExpr.IncludeExpression), expression.ReferenceSearchParameter);

            IReadOnlyCollection<string> referencedTypes = expression.ReferencedTypes?.ToList();

            return new FhirExpr.IncludeExpression(
                expression.ResourceTypes,
                referenceSearchParameter,
                expression.SourceResourceType,
                expression.TargetResourceType,
                referencedTypes,
                expression.WildCard,
                expression.Reversed,
                expression.Iterate);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitCompartment(IgnixaExpr.CompartmentSearchExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            // Ignixa's lowered model exposes only a single compartment node. The FHIR Server distinction between a
            // standard compartment and a SMART compartment is not recoverable from the lowered tree, so the bridge
            // always emits a standard compartment. SMART compartment scoping is applied separately by the FHIR Server
            // request pipeline and is validated by the divergence guard at the Cosmos routing boundary.
            string[] filteredResourceTypes = expression.FilteredResourceTypes?.ToArray() ?? Array.Empty<string>();
            return new FhirExpr.CompartmentSearchExpression(expression.CompartmentType, expression.CompartmentId, filteredResourceTypes);
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitSortParameter(IgnixaExpr.SortExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            // Sort direction is represented in SearchOptions.Sort and never travels through the Cosmos expression
            // tree. The FHIR Server SortExpression cannot carry the sort order, so fail explicitly rather than lose it.
            throw new IgnixaExpressionBridgeException(
                nameof(IgnixaExpr.SortExpression),
                expression.Parameter?.Code,
                "Sort expressions are represented by SearchOptions.Sort and are not supported in the Cosmos expression tree.");
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitNotReferenced(IgnixaExpr.NotReferencedExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            if (expression.IsFullWildcard)
            {
                return new FhirExpr.NotReferencedExpression(referenceSearchParameter: null, sourceResourceType: null, wildCard: true);
            }

            // A path-scoped or reference-parameter-scoped not-referenced predicate cannot be represented faithfully
            // because the FHIR Server model expects a resolved reference search parameter rather than a raw path.
            throw new IgnixaExpressionBridgeException(
                nameof(IgnixaExpr.NotReferencedExpression),
                expression.ReferencePath,
                "Only full-wildcard not-referenced expressions can be bridged to the FHIR Server Cosmos model.");
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitPatientEverything(IgnixaExpr.PatientEverythingExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            // Patient $everything is orchestrated outside the Cosmos expression tree and has no FHIR Server expression
            // node, so it cannot be bridged.
            throw new IgnixaExpressionBridgeException(
                nameof(IgnixaExpr.PatientEverythingExpression),
                parameterCode: null,
                "Patient everything expressions have no FHIR Server expression-tree representation.");
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitSearchParameterPredicate(IgnixaExpr.SearchParameterPredicateExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            // Predicate nodes must be removed by LegacyExpressionLowerer before bridging. Encountering one indicates
            // the caller passed an un-lowered expression.
            throw new IgnixaExpressionBridgeException(
                nameof(IgnixaExpr.SearchParameterPredicateExpression),
                expression.Parameter?.Code,
                "Search parameter predicate nodes must be lowered with LegacyExpressionLowerer before bridging.");
        }

        /// <inheritdoc />
        public FhirExpr.Expression VisitCompositeComponent(IgnixaExpr.CompositeComponentExpression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            // Composite component nodes must be removed by LegacyExpressionLowerer before bridging.
            throw new IgnixaExpressionBridgeException(
                nameof(IgnixaExpr.CompositeComponentExpression),
                expression.ComponentSearchParameter?.Code,
                "Composite component nodes must be lowered with LegacyExpressionLowerer before bridging.");
        }

        private FhirExpr.Expression Convert(IgnixaExpr.Expression expression, string context)
        {
            EnsureArg.IsNotNull(expression, nameof(expression));

            return expression.AcceptVisitor(this, context);
        }

        private FhirExpr.Expression[] ConvertOperands(IReadOnlyList<IgnixaExpr.Expression> operands, string context)
        {
            EnsureArg.IsNotNull(operands, nameof(operands));

            var converted = new FhirExpr.Expression[operands.Count];
            for (int i = 0; i < operands.Count; i++)
            {
                converted[i] = Convert(operands[i], context);
            }

            return converted;
        }

        private FhirSpi ResolveParameter(string nodeType, IgnixaSpi ignixaParameter)
        {
            if (ignixaParameter == null)
            {
                throw new IgnixaExpressionBridgeException(nodeType, parameterCode: null, "The Ignixa search parameter metadata was null.");
            }

            Uri url = ignixaParameter.Url;
            if (url != null && _searchParameterDefinitionManager.TryGetSearchParameter(url.ToString(), out FhirSpi resolved))
            {
                return resolved;
            }

            throw new IgnixaExpressionBridgeException(
                nodeType,
                ignixaParameter.Code,
                $"The FHIR Server search parameter definition could not be resolved for URL '{url?.ToString() ?? "(null)"}'.");
        }
    }
}
