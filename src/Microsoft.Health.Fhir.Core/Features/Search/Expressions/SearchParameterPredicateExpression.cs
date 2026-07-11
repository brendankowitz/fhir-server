// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.Core.Features.Search.Expressions
{
    /// <summary>
    /// Represents a semantic FHIR search predicate that captures a search parameter, an optional
    /// modifier, a comparator, an optional component index, and a normalized search value.
    /// This node operates at the semantic level and must be lowered to legacy SQL/Cosmos tree nodes
    /// before reaching a backend expression visitor.
    /// </summary>
    public sealed class SearchParameterPredicateExpression : Expression
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SearchParameterPredicateExpression"/> class.
        /// </summary>
        /// <param name="parameter">The search parameter this predicate is bound to. Must not be <c>null</c>.</param>
        /// <param name="modifier">The optional search modifier applied to the parameter, or <c>null</c> if no modifier is present.</param>
        /// <param name="comparator">The FHIR search comparator (e.g. <see cref="SearchComparator.Eq"/>). Must be a defined enum value.</param>
        /// <param name="componentIndex">The zero-based index of the composite component, or <c>null</c> for non-composite parameters. Must not be negative.</param>
        /// <param name="value">The normalized search value. Must not be <c>null</c>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameter"/> or <paramref name="value"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="comparator"/> is not a defined enum value, or <paramref name="componentIndex"/> is negative.</exception>
        public SearchParameterPredicateExpression(
            SearchParameterInfo parameter,
            SearchModifier modifier,
            SearchComparator comparator,
            int? componentIndex,
            ISearchValue value)
        {
            EnsureArg.IsNotNull(parameter, nameof(parameter));
            EnsureArg.IsNotNull(value, nameof(value));

            if (!Enum.IsDefined<SearchComparator>(comparator))
            {
                throw new ArgumentOutOfRangeException(nameof(comparator), comparator, "SearchComparator value is not defined.");
            }

            if (componentIndex.HasValue && componentIndex.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(componentIndex), componentIndex, "Component index must not be negative.");
            }

            Parameter = parameter;
            Modifier = modifier;
            Comparator = comparator;
            ComponentIndex = componentIndex;
            Value = value;
        }

        /// <summary>
        /// Gets the search parameter this predicate is bound to.
        /// </summary>
        public SearchParameterInfo Parameter { get; }

        /// <summary>
        /// Gets the optional search modifier applied to the parameter, or <c>null</c> if no modifier is present.
        /// </summary>
        public SearchModifier Modifier { get; }

        /// <summary>
        /// Gets the FHIR search comparator.
        /// </summary>
        public SearchComparator Comparator { get; }

        /// <summary>
        /// Gets the zero-based index of the composite component, or <c>null</c> for non-composite parameters.
        /// </summary>
        public int? ComponentIndex { get; }

        /// <summary>
        /// Gets the normalized search value associated with this predicate.
        /// </summary>
        public ISearchValue Value { get; }

        /// <inheritdoc />
        public override TOutput AcceptVisitor<TContext, TOutput>(IExpressionVisitor<TContext, TOutput> visitor, TContext context)
        {
            EnsureArg.IsNotNull(visitor, nameof(visitor));
            return visitor.VisitSearchParameterPredicate(this, context);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            string componentPart = ComponentIndex.HasValue ? $"[{ComponentIndex}]." : string.Empty;
            string modifierPart = Modifier != null ? $":{Modifier}" : string.Empty;

            // TokenSearchValue.ToString() does not render the Text property; use it directly when present.
            string valuePart = Value is TokenSearchValue tsv && tsv.Text != null
                ? tsv.Text
                : Value.ToString();

            return $"(SemanticPredicate {componentPart}{Parameter.Code}{modifierPart} {Comparator} {valuePart})";
        }

        /// <inheritdoc />
        /// <remarks>
        /// Conservative in this phase: includes the normalized <see cref="Value"/> rather than
        /// collapsing all values of the same type. This ensures caching / deduplication is safe
        /// until a lowering pass can reason about value equivalence more precisely.
        /// </remarks>
        public override void AddValueInsensitiveHashCode(ref HashCode hashCode)
        {
            hashCode.Add(typeof(SearchParameterPredicateExpression));
            hashCode.Add(Parameter);
            hashCode.Add(Modifier);
            hashCode.Add(Comparator);
            hashCode.Add(ComponentIndex);
            hashCode.Add(Value);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Conservative in this phase: includes the normalized <see cref="Value"/> in the comparison.
        /// </remarks>
        public override bool ValueInsensitiveEquals(Expression other)
        {
            return other is SearchParameterPredicateExpression sppe &&
                   sppe.Parameter.Equals(Parameter) &&
                   sppe.Modifier == Modifier &&
                   sppe.Comparator == Comparator &&
                   sppe.ComponentIndex == ComponentIndex &&
                   (sppe.Value?.Equals(Value) ?? Value == null);
        }
    }
}
