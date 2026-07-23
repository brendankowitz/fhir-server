// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.CosmosDb.Features.Search;
using NSubstitute;
using Xunit;
using FhirExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;
using FhirIncludeExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.IncludeExpression;
using IgnixaExpr = Ignixa.Search.Expressions.Expression;

namespace Microsoft.Health.Fhir.CosmosDb.UnitTests.Features.Search
{
    /// <summary>
    /// Service-seam tests proving that <see cref="IgnixaCosmosExpressionRouter"/> (the routing logic that
    /// <c>FhirCosmosSearchService</c> invokes before the Cosmos pipeline runs) fails fast on unsupported canonical
    /// semantics, routes supported canonical semantics through the bridge, and retains the legacy projection on a
    /// genuine structural divergence — without the null-reference / NOT-shape defects of the old equality guard.
    /// </summary>
    [Trait(Microsoft.Health.Test.Utilities.Traits.OwningTeam, Microsoft.Health.Fhir.Tests.Common.OwningTeam.Fhir)]
    [Trait(Microsoft.Health.Test.Utilities.Traits.Category, Microsoft.Health.Fhir.Tests.Common.Categories.Search)]
    public class IgnixaCosmosExpressionRouterTests
    {
        private readonly IIgnixaLegacyExpressionBridge _bridge = Substitute.For<IIgnixaLegacyExpressionBridge>();

        [Fact]
        public void GivenNoCanonicalExpression_WhenRouted_ThenLegacyRetainedAndBridgeNotInvoked()
        {
            FhirExpression legacy = FhirExpression.StringEquals(FieldName.String, null, "x", true);
            SearchOptions options = CreateSearchOptions(ignixa: null, legacy: legacy);

            CreateRouter().Route(options);

            Assert.Same(legacy, options.Expression);
            _bridge.DidNotReceiveWithAnyArgs().Convert(default);
        }

        [Fact]
        public void GivenUnsupportedCanonicalSemantics_WhenRouted_ThenThrowsAndDoesNotExecuteCosmos()
        {
            FhirExpression legacy = FhirExpression.StringEquals(FieldName.String, null, "x", true);
            SearchOptions options = CreateSearchOptions(ignixa: CreateCanonicalFixture(), legacy: legacy);
            _bridge.Convert(Arg.Any<IgnixaExpr>()).Returns(_ => throw new SearchOperationNotSupportedException("unsupported node"));

            // Fail-fast: unsupported canonical semantics must throw before Cosmos runs, and must not silently fall back.
            Assert.Throws<SearchOperationNotSupportedException>(() => CreateRouter().Route(options));
            Assert.Same(legacy, options.Expression);
        }

        [Fact]
        public void GivenSupportedCanonicalMatchingLegacy_WhenRouted_ThenBridgedExpressionFedToPipeline()
        {
            FhirExpression legacy = FhirExpression.StringEquals(FieldName.String, null, "legacy", true);
            FhirExpression bridged = FhirExpression.StringEquals(FieldName.String, null, "bridged", true);
            SearchOptions options = CreateSearchOptions(ignixa: CreateCanonicalFixture(), legacy: legacy);
            _bridge.Convert(Arg.Any<IgnixaExpr>()).Returns(bridged);

            CreateRouter().Route(options);

            // Supported canonical semantics: the bridged expression is what proceeds to the Cosmos query builder.
            Assert.Same(bridged, options.Expression);
        }

        [Fact]
        public void GivenSmartCompartmentDivergence_WhenRouted_ThenLegacyProjectionRetained()
        {
            FhirExpression legacy = FhirExpression.SmartCompartmentSearch("Patient", "123");
            FhirExpression bridged = FhirExpression.CompartmentSearch("Patient", "123");
            SearchOptions options = CreateSearchOptions(ignixa: CreateCanonicalFixture(), legacy: legacy);
            _bridge.Convert(Arg.Any<IgnixaExpr>()).Returns(bridged);

            CreateRouter().Route(options);

            // A plain compartment must not replace a SMART compartment; the security-scoped legacy projection stays.
            Assert.Same(legacy, options.Expression);
        }

        [Fact]
        public void GivenWildcardIncludeShapes_WhenRouted_ThenBridgedUsedWithoutThrowing()
        {
            FhirExpression legacy = CreateWildcardInclude();
            FhirExpression bridged = CreateWildcardInclude();
            SearchOptions options = CreateSearchOptions(ignixa: CreateCanonicalFixture(), legacy: legacy);
            _bridge.Convert(Arg.Any<IgnixaExpr>()).Returns(bridged);

            // The old guard threw an NRE for wildcard includes (null ReferenceSearchParameter); this must not throw.
            CreateRouter().Route(options);

            Assert.Same(bridged, options.Expression);
        }

        [Fact]
        public void GivenEquivalentNotShapes_WhenRouted_ThenBridgedUsed()
        {
            FhirExpression legacy = FhirExpression.Not(FhirExpression.StringEquals(FieldName.String, null, "legacy", true));
            FhirExpression bridged = FhirExpression.Not(FhirExpression.StringEquals(FieldName.String, null, "bridged", true));
            SearchOptions options = CreateSearchOptions(ignixa: CreateCanonicalFixture(), legacy: legacy);
            _bridge.Convert(Arg.Any<IgnixaExpr>()).Returns(bridged);

            // The old guard reported equivalent NOT shapes as unequal; the router must now route through the bridge.
            CreateRouter().Route(options);

            Assert.Same(bridged, options.Expression);
        }

        private IgnixaCosmosExpressionRouter CreateRouter() => new IgnixaCosmosExpressionRouter(_bridge, NullLogger.Instance);

        private static IgnixaExpr CreateCanonicalFixture() =>
            new Ignixa.Search.Expressions.NotReferencedExpression(sourceResourceType: "Patient", referencePath: "Patient.link");

        private static FhirIncludeExpression CreateWildcardInclude() =>
            new FhirIncludeExpression(
                resourceTypes: new[] { "Patient" },
                referenceSearchParameter: null,
                sourceResourceType: "Patient",
                targetResourceType: null,
                referencedTypes: null,
                wildCard: true,
                reversed: false,
                iterate: false);

        private static SearchOptions CreateSearchOptions(IgnixaExpr ignixa, FhirExpression legacy)
        {
            var options = new SearchOptions
            {
                Expression = legacy,
            };

            if (ignixa != null)
            {
                options.IgnixaOptions = new Ignixa.Search.Models.SearchOptions { Expression = ignixa };
            }

            return options;
        }
    }
}
