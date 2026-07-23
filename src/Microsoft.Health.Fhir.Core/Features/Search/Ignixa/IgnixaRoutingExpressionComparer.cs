// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    /// <summary>
    /// Provides a null-safe, value-insensitive, node-type-aware structural comparison of two
    /// FHIR Server <see cref="Expression"/> trees for the purpose of routing decisions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This comparer exists specifically to decide whether an expression produced by lowering and
    /// bridging the canonical Ignixa expression is structurally equivalent to the legacy projection
    /// that the <c>SearchOptionsFactory</c> assembled. It intentionally compares node <em>shape</em>
    /// (node types, Boolean operators, chain direction/target types, include mode/iterate flags,
    /// missing/not semantics, field names and composite component ordering) and ignores literal
    /// values, because the bridge is responsible for supplying faithful values that drive the query.
    /// </para>
    /// <para>
    /// Unlike <see cref="Expression.ValueInsensitiveEquals(Expression)"/>, this comparer is
    /// null-safe (for example, wildcard includes whose <c>ReferenceSearchParameter</c> is
    /// <see langword="null"/>), correctly recurses through <see cref="NotExpression"/>, and treats
    /// distinct concrete node types (such as <see cref="CompartmentSearchExpression"/> versus
    /// <see cref="SmartCompartmentSearchExpression"/>) as non-equivalent so that security-scoped
    /// SMART routing is never silently replaced by a plain compartment query. Unknown node types are
    /// conservatively reported as non-equivalent.
    /// </para>
    /// </remarks>
    internal static class IgnixaRoutingExpressionComparer
    {
        /// <summary>
        /// Determines whether two expression trees are structurally equivalent for routing purposes.
        /// </summary>
        /// <param name="left">The first expression (typically the bridged expression). May be <see langword="null"/>.</param>
        /// <param name="right">The second expression (typically the legacy projection). May be <see langword="null"/>.</param>
        /// <returns>
        /// <see langword="true"/> if the two trees have equivalent structure (ignoring literal values);
        /// otherwise <see langword="false"/>. Two <see langword="null"/> references are considered equivalent.
        /// </returns>
        internal static bool AreStructurallyEquivalent(Expression left, Expression right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null)
            {
                return false;
            }

            // A different concrete node type is a real structural divergence (for example, a
            // SmartCompartmentSearchExpression must never be treated as a plain CompartmentSearchExpression).
            if (left.GetType() != right.GetType())
            {
                return false;
            }

            // The SMART V2 scope-union flag lives on the base Expression and drives SMART-specific scope filtering.
            // The bridge cannot reproduce it (Ignixa has no equivalent), so a flagged legacy expression must never be
            // treated as equivalent to an unflagged bridged expression; force the legacy projection on any mismatch.
            if (left.IsSmartV2UnionExpressionForScopesSearchParameters != right.IsSmartV2UnionExpressionForScopesSearchParameters)
            {
                return false;
            }

            switch (left)
            {
                case SearchParameterExpression l:
                {
                    var r = (SearchParameterExpression)right;
                    return ParametersEqual(l.Parameter, r.Parameter)
                        && AreStructurallyEquivalent(l.Expression, r.Expression);
                }

                case MissingSearchParameterExpression l:
                {
                    var r = (MissingSearchParameterExpression)right;
                    return ParametersEqual(l.Parameter, r.Parameter)
                        && l.IsMissing == r.IsMissing;
                }

                case NotExpression l:
                {
                    var r = (NotExpression)right;
                    return AreStructurallyEquivalent(l.Expression, r.Expression);
                }

                case MultiaryExpression l:
                {
                    var r = (MultiaryExpression)right;
                    return l.MultiaryOperation == r.MultiaryOperation
                        && OperandsEqual(l.Expressions, r.Expressions);
                }

                case UnionExpression l:
                {
                    var r = (UnionExpression)right;
                    return l.Operator == r.Operator
                        && OperandsEqual(l.Expressions, r.Expressions);
                }

                case ChainedExpression l:
                {
                    var r = (ChainedExpression)right;
                    return OrderedTypesEqual(l.ResourceTypes, r.ResourceTypes)
                        && ParametersEqual(l.ReferenceSearchParameter, r.ReferenceSearchParameter)
                        && OrderedTypesEqual(l.TargetResourceTypes, r.TargetResourceTypes)
                        && l.Reversed == r.Reversed
                        && AreStructurallyEquivalent(l.Expression, r.Expression);
                }

                case IncludeExpression l:
                {
                    var r = (IncludeExpression)right;
                    return ParametersEqual(l.ReferenceSearchParameter, r.ReferenceSearchParameter)
                        && string.Equals(l.SourceResourceType, r.SourceResourceType, StringComparison.Ordinal)
                        && string.Equals(l.TargetResourceType, r.TargetResourceType, StringComparison.Ordinal)
                        && l.WildCard == r.WildCard
                        && l.Reversed == r.Reversed
                        && l.Iterate == r.Iterate
                        && OrderedTypesEqual(l.ResourceTypes, r.ResourceTypes)
                        && NullableSetEqual(l.ReferencedTypes, r.ReferencedTypes)
                        && NullableSetEqual(l.AllowedResourceTypesByScope, r.AllowedResourceTypesByScope);
                }

                // NOTE: SmartCompartmentSearchExpression derives from CompartmentSearchExpression, but the
                // GetType() guard above guarantees both operands are the exact same concrete type here.
                case CompartmentSearchExpression l:
                {
                    var r = (CompartmentSearchExpression)right;
                    return string.Equals(l.CompartmentType, r.CompartmentType, StringComparison.Ordinal)
                        && string.Equals(l.CompartmentId, r.CompartmentId, StringComparison.Ordinal)
                        && NullableSetEqual(l.FilteredResourceTypes, r.FilteredResourceTypes);
                }

                case NotReferencedExpression l:
                {
                    var r = (NotReferencedExpression)right;
                    return ParametersEqual(l.ReferenceSearchParameter, r.ReferenceSearchParameter)
                        && string.Equals(l.SourceResourceType, r.SourceResourceType, StringComparison.Ordinal)
                        && l.WildCard == r.WildCard;
                }

                case StringExpression l:
                {
                    var r = (StringExpression)right;
                    return l.StringOperator == r.StringOperator
                        && l.FieldName == r.FieldName
                        && l.ComponentIndex == r.ComponentIndex
                        && l.IgnoreCase == r.IgnoreCase;
                }

                case BinaryExpression l:
                {
                    var r = (BinaryExpression)right;
                    return l.BinaryOperator == r.BinaryOperator
                        && l.FieldName == r.FieldName
                        && l.ComponentIndex == r.ComponentIndex;
                }

                // Covers InExpression<T>, MissingFieldExpression and any other field-scoped node whose
                // routing shape is fully described by its field name and composite component index.
                case IFieldExpression l:
                {
                    var r = (IFieldExpression)right;
                    return l.FieldName == r.FieldName
                        && l.ComponentIndex == r.ComponentIndex;
                }

                default:
                    // Unknown node type: we cannot prove equivalence, so stay conservative and report
                    // divergence rather than risk broadening or altering the query.
                    return false;
            }
        }

        private static bool OperandsEqual(IReadOnlyList<Expression> left, IReadOnlyList<Expression> right)
        {
            if (left is null || right is null)
            {
                return ReferenceEquals(left, right);
            }

            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!AreStructurallyEquivalent(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ParametersEqual(SearchParameterInfo left, SearchParameterInfo right)
        {
            if (left is null || right is null)
            {
                return ReferenceEquals(left, right);
            }

            return string.Equals(left.Code, right.Code, StringComparison.Ordinal)
                && Equals(left.Url, right.Url);
        }

        /// <summary>
        /// Compares two ordered resource-type arrays positionally. Downstream Cosmos filtering consumes these arrays by
        /// position (for example <c>.First()</c>), so a reordering is a genuine query-shape difference. The comparison
        /// preserves the null-versus-empty distinction (a null collection is never equal to an empty one).
        /// </summary>
        private static bool OrderedTypesEqual(string[] left, string[] right)
        {
            if (left is null || right is null)
            {
                return ReferenceEquals(left, right);
            }

            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Compares two resource-type collections as order-insensitive sets while preserving the null-versus-empty
        /// distinction. This matters for security-scoping collections such as <c>AllowedResourceTypesByScope</c>
        /// (a null collection means "unrestricted" whereas an empty collection means "no access") and for wildcard
        /// <c>ReferencedTypes</c> semantics.
        /// </summary>
        private static bool NullableSetEqual(IEnumerable<string> left, IEnumerable<string> right)
        {
            if (left is null || right is null)
            {
                return ReferenceEquals(left, right);
            }

            var leftSet = new HashSet<string>(left, StringComparer.Ordinal);
            var rightSet = new HashSet<string>(right, StringComparer.Ordinal);

            return leftSet.SetEquals(rightSet);
        }
    }
}
