// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Xunit;
using FhirExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.Ignixa
{
    /// <summary>
    /// Regression tests for <see cref="IgnixaRoutingExpressionComparer"/>, the null-safe / node-type-aware
    /// structural comparer used to decide whether the bridged Ignixa expression may replace the legacy projection.
    /// </summary>
    [Trait(Microsoft.Health.Test.Utilities.Traits.OwningTeam, Microsoft.Health.Fhir.Tests.Common.OwningTeam.Fhir)]
    [Trait(Microsoft.Health.Test.Utilities.Traits.Category, Microsoft.Health.Fhir.Tests.Common.Categories.Search)]
    public class IgnixaRoutingExpressionComparerTests
    {
        [Fact]
        public void GivenTwoWildcardIncludes_WhenCompared_ThenEquivalentWithoutThrowing()
        {
            // Wildcard includes have a null ReferenceSearchParameter. The legacy ValueInsensitiveEquals throws an
            // NRE for this shape; the routing comparer must be null-safe and report equivalence.
            IncludeExpression left = CreateWildcardInclude();
            IncludeExpression right = CreateWildcardInclude();

            Assert.True(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenWildcardIncludesWithDifferentAllowedScope_WhenCompared_ThenNotEquivalent()
        {
            IncludeExpression left = CreateWildcardInclude(allowedResourceTypesByScope: new[] { "Patient" });
            IncludeExpression right = CreateWildcardInclude(allowedResourceTypesByScope: new[] { "Observation" });

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenEquivalentNotShapes_WhenCompared_ThenEquivalent()
        {
            FhirExpression left = FhirExpression.Not(FhirExpression.StringEquals(FieldName.String, null, "a", true));
            FhirExpression right = FhirExpression.Not(FhirExpression.StringEquals(FieldName.String, null, "DIFFERENT-VALUE", true));

            // NotExpression equality was buggy in ValueInsensitiveEquals (reported equivalent NOT shapes as unequal).
            Assert.True(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenDifferentNotShapes_WhenCompared_ThenNotEquivalent()
        {
            FhirExpression left = FhirExpression.Not(FhirExpression.StringEquals(FieldName.String, null, "a", true));
            FhirExpression right = FhirExpression.Not(FhirExpression.StartsWith(FieldName.String, null, "a", true));

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenSmartCompartmentVersusPlainCompartment_WhenCompared_ThenNotEquivalent()
        {
            // Security-critical: a SMART compartment must never be treated as a plain compartment, even when the
            // compartment type and id match, because they are handled by different Cosmos rewriters.
            FhirExpression smart = FhirExpression.SmartCompartmentSearch("Patient", "123");
            FhirExpression plain = FhirExpression.CompartmentSearch("Patient", "123");

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(smart, plain));
        }

        [Fact]
        public void GivenSameCompartment_WhenCompared_ThenEquivalent()
        {
            FhirExpression left = FhirExpression.CompartmentSearch("Patient", "123", "Observation");
            FhirExpression right = FhirExpression.CompartmentSearch("Patient", "123", "Observation");

            Assert.True(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenStringExpressionsDifferingOnlyByValue_WhenCompared_ThenEquivalent()
        {
            FhirExpression left = FhirExpression.StringEquals(FieldName.String, null, "Smith", true);
            FhirExpression right = FhirExpression.StringEquals(FieldName.String, null, "Jones", true);

            Assert.True(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenStringExpressionsDifferingByOperator_WhenCompared_ThenNotEquivalent()
        {
            FhirExpression left = FhirExpression.StringEquals(FieldName.String, null, "Smith", true);
            FhirExpression right = FhirExpression.StartsWith(FieldName.String, null, "Smith", true);

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenNestedAndOrWithNot_WhenEquivalent_ThenTrue()
        {
            FhirExpression Build(string leaf) => FhirExpression.And(
                FhirExpression.Or(
                    FhirExpression.StringEquals(FieldName.String, null, leaf, true),
                    FhirExpression.Not(FhirExpression.StartsWith(FieldName.String, null, leaf, false))),
                FhirExpression.Missing(FieldName.TokenCode, null));

            Assert.True(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(Build("x"), Build("y")));
        }

        [Fact]
        public void GivenNestedShapes_WhenOperandDiffers_ThenNotEquivalent()
        {
            FhirExpression left = FhirExpression.And(
                FhirExpression.StringEquals(FieldName.String, null, "x", true),
                FhirExpression.Missing(FieldName.TokenCode, null));

            FhirExpression right = FhirExpression.And(
                FhirExpression.StringEquals(FieldName.String, null, "x", true),
                FhirExpression.StartsWith(FieldName.String, null, "x", true));

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenChainsDifferingByDirection_WhenCompared_ThenNotEquivalent()
        {
            var parameter = new SearchParameterInfo("subject", "subject");
            FhirExpression forward = FhirExpression.Chained(new[] { "Observation" }, parameter, new[] { "Patient" }, false, FhirExpression.StringEquals(FieldName.String, null, "x", true));
            FhirExpression reverse = FhirExpression.Chained(new[] { "Observation" }, parameter, new[] { "Patient" }, true, FhirExpression.StringEquals(FieldName.String, null, "x", true));

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(forward, reverse));
        }

        [Fact]
        public void GivenNullReferences_WhenCompared_ThenBothNullEquivalentAndSingleNullNot()
        {
            FhirExpression expression = FhirExpression.StringEquals(FieldName.String, null, "x", true);

            Assert.True(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(null, null));
            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(expression, null));
            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(null, expression));
        }

        [Fact]
        public void GivenWildcardIncludes_WhenAllowedScopeNullVersusEmpty_ThenNotEquivalent()
        {
            // null AllowedResourceTypesByScope means "unrestricted"; an empty collection means "no access".
            // The bridge omits this restriction, so an empty legacy restriction must never be treated as equivalent
            // to an unrestricted bridged include.
            IncludeExpression unrestricted = CreateWildcardInclude(allowedResourceTypesByScope: null);
            IncludeExpression noAccess = CreateWildcardInclude(allowedResourceTypesByScope: Array.Empty<string>());

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(unrestricted, noAccess));
        }

        [Fact]
        public void GivenIncludes_WhenReferencedTypesNullVersusEmpty_ThenNotEquivalent()
        {
            IncludeExpression nullReferencedTypes = CreateWildcardInclude(referencedTypes: null);
            IncludeExpression emptyReferencedTypes = CreateWildcardInclude(referencedTypes: Array.Empty<string>());

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(nullReferencedTypes, emptyReferencedTypes));
        }

        [Fact]
        public void GivenIncludes_WhenResourceTypesReordered_ThenNotEquivalent()
        {
            // Cosmos filtering uses ResourceTypes positionally (.First()); reordering changes the generated query.
            var left = new IncludeExpression(new[] { "Patient", "Group" }, null, "Patient", null, null, wildCard: true, reversed: false, iterate: false);
            var right = new IncludeExpression(new[] { "Group", "Patient" }, null, "Patient", null, null, wildCard: true, reversed: false, iterate: false);

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenChains_WhenTargetTypesReordered_ThenNotEquivalent()
        {
            var parameter = new SearchParameterInfo("subject", "subject");
            FhirExpression predicate = FhirExpression.StringEquals(FieldName.String, null, "x", true);
            FhirExpression left = FhirExpression.Chained(new[] { "Observation" }, parameter, new[] { "Patient", "Group" }, false, predicate);
            FhirExpression right = FhirExpression.Chained(new[] { "Observation" }, parameter, new[] { "Group", "Patient" }, false, predicate);

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        [Fact]
        public void GivenUnions_WhenSmartV2ScopeFlagDiffers_ThenNotEquivalent()
        {
            // A SMART V2 scope union drives SMART-specific scope filtering. The bridge cannot set this flag, so a
            // flagged legacy union must never be treated as equivalent to an unflagged bridged union.
            var flagged = new UnionExpression(UnionOperator.All, new[] { FhirExpression.StringEquals(FieldName.String, null, "x", true) })
            {
                IsSmartV2UnionExpressionForScopesSearchParameters = true,
            };
            var unflagged = new UnionExpression(UnionOperator.All, new[] { FhirExpression.StringEquals(FieldName.String, null, "x", true) });

            Assert.False(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(flagged, unflagged));
        }

        [Fact]
        public void GivenSameCompartmentWithSameFilteredTypes_WhenCompared_ThenEquivalent()
        {
            // Regression guard: two empty (non-null) filtered-type collections must still compare equivalent.
            FhirExpression left = FhirExpression.CompartmentSearch("Patient", "123");
            FhirExpression right = FhirExpression.CompartmentSearch("Patient", "123");

            Assert.True(IgnixaRoutingExpressionComparer.AreStructurallyEquivalent(left, right));
        }

        private static IncludeExpression CreateWildcardInclude(string[] allowedResourceTypesByScope = null, string[] referencedTypes = null)
        {
            return new IncludeExpression(
                resourceTypes: new[] { "Patient" },
                referenceSearchParameter: null,
                sourceResourceType: "Patient",
                targetResourceType: null,
                referencedTypes: referencedTypes,
                wildCard: true,
                reversed: false,
                iterate: false,
                allowedResourceTypesByScope: allowedResourceTypesByScope);
        }
    }
}
