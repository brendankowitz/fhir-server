// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ignixa.Specification.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;
using FhirBinaryExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.BinaryExpression;
using FhirBinaryOperator = Microsoft.Health.Fhir.Core.Features.Search.Expressions.BinaryOperator;
using FhirChainedExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.ChainedExpression;
using FhirCompartmentSearchExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.CompartmentSearchExpression;
using FhirExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;
using FhirFieldName = Microsoft.Health.Fhir.Core.Features.Search.Expressions.FieldName;
using FhirIncludeExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.IncludeExpression;
using FhirInExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.InExpression<string>;
using FhirMissingFieldExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.MissingFieldExpression;
using FhirMissingSearchParameterExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.MissingSearchParameterExpression;
using FhirMultiaryExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.MultiaryExpression;
using FhirMultiaryOperator = Microsoft.Health.Fhir.Core.Features.Search.Expressions.MultiaryOperator;
using FhirNotExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.NotExpression;
using FhirNotReferencedExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.NotReferencedExpression;
using FhirSearchParameterExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.SearchParameterExpression;
using FhirSpi = Microsoft.Health.Fhir.Core.Models.SearchParameterInfo;
using FhirStringExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.StringExpression;
using FhirStringOperator = Microsoft.Health.Fhir.Core.Features.Search.Expressions.StringOperator;
using FhirUnionExpression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.UnionExpression;
using IgnixaExpr = Ignixa.Search.Expressions.Expression;
using IgnixaFieldName = Ignixa.Search.Expressions.FieldName;
using IgnixaSpi = Ignixa.Search.Models.SearchParameterInfo;
using IgnixaUnionOperator = Ignixa.Search.Expressions.UnionOperator;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.Ignixa
{
    [Trait(Traits.OwningTeam, Microsoft.Health.Fhir.Tests.Common.OwningTeam.Fhir)]
    [Trait(Traits.Category, Microsoft.Health.Fhir.Tests.Common.Categories.Search)]
    public class IgnixaLegacyExpressionBridgeTests
    {
        private const string NameUrl = "http://hl7.org/fhir/SearchParameter/Patient-name";
        private const string SubjectUrl = "http://hl7.org/fhir/SearchParameter/Observation-subject";
        private const string GeneralPractitionerUrl = "http://hl7.org/fhir/SearchParameter/Patient-general-practitioner";

        private readonly FhirSpi _nameParameter = new FhirSpi("name", "name", global::Microsoft.Health.Fhir.ValueSets.SearchParamType.String, new Uri(NameUrl));
        private readonly FhirSpi _subjectParameter = new FhirSpi("subject", "subject", global::Microsoft.Health.Fhir.ValueSets.SearchParamType.Reference, new Uri(SubjectUrl));
        private readonly FhirSpi _generalPractitionerParameter = new FhirSpi("general-practitioner", "general-practitioner", global::Microsoft.Health.Fhir.ValueSets.SearchParamType.Reference, new Uri(GeneralPractitionerUrl));

        private readonly IgnixaSpi _nameIgnixaParameter = CreateIgnixaParameter("name", NameUrl);
        private readonly IgnixaSpi _subjectIgnixaParameter = CreateIgnixaParameter("subject", SubjectUrl);
        private readonly IgnixaSpi _generalPractitionerIgnixaParameter = CreateIgnixaParameter("general-practitioner", GeneralPractitionerUrl);

        [Fact]
        public void GivenSearchParameterWithStringPredicate_WhenConverted_ThenSearchParameterExpressionMatches()
        {
            IgnixaExpr input = IgnixaExpr.SearchParameter(
                _nameIgnixaParameter,
                IgnixaExpr.StartsWith(IgnixaFieldName.String, null, "Smith", true));

            FhirExpression result = CreateBridge().Convert(input);

            var searchParameter = Assert.IsType<FhirSearchParameterExpression>(result);
            Assert.Same(_nameParameter, searchParameter.Parameter);
            var stringExpression = Assert.IsType<FhirStringExpression>(searchParameter.Expression);
            Assert.Equal(FhirStringOperator.StartsWith, stringExpression.StringOperator);
            Assert.Equal(FhirFieldName.String, stringExpression.FieldName);
            Assert.Equal("Smith", stringExpression.Value);
            Assert.True(stringExpression.IgnoreCase);
        }

        [Fact]
        public void GivenNestedAndOrWithNot_WhenConverted_ThenBooleanShapeAndOperandOrderPreserved()
        {
            IgnixaExpr input = IgnixaExpr.And(
                IgnixaExpr.StringEquals(IgnixaFieldName.TokenCode, null, "a", false),
                IgnixaExpr.Or(
                    IgnixaExpr.Not(IgnixaExpr.StringEquals(IgnixaFieldName.TokenCode, null, "b", false)),
                    IgnixaExpr.StringEquals(IgnixaFieldName.TokenCode, null, "c", false)));

            FhirExpression result = CreateBridge().Convert(input);

            var and = Assert.IsType<FhirMultiaryExpression>(result);
            Assert.Equal(FhirMultiaryOperator.And, and.MultiaryOperation);
            Assert.Equal(2, and.Expressions.Count);
            Assert.Equal("a", Assert.IsType<FhirStringExpression>(and.Expressions[0]).Value);

            var or = Assert.IsType<FhirMultiaryExpression>(and.Expressions[1]);
            Assert.Equal(FhirMultiaryOperator.Or, or.MultiaryOperation);
            Assert.Equal(2, or.Expressions.Count);

            var not = Assert.IsType<FhirNotExpression>(or.Expressions[0]);
            Assert.Equal("b", Assert.IsType<FhirStringExpression>(not.Expression).Value);
            Assert.Equal("c", Assert.IsType<FhirStringExpression>(or.Expressions[1]).Value);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void GivenMissingSearchParameter_WhenConverted_ThenMissingSemanticsPreserved(bool isMissing)
        {
            IgnixaExpr input = IgnixaExpr.MissingSearchParameter(_nameIgnixaParameter, isMissing);

            FhirExpression result = CreateBridge().Convert(input);

            var missing = Assert.IsType<FhirMissingSearchParameterExpression>(result);
            Assert.Same(_nameParameter, missing.Parameter);
            Assert.Equal(isMissing, missing.IsMissing);
        }

        [Fact]
        public void GivenMissingField_WhenConverted_ThenFieldAndComponentPreserved()
        {
            IgnixaExpr input = IgnixaExpr.Missing(IgnixaFieldName.TokenSystem, 2);

            FhirExpression result = CreateBridge().Convert(input);

            var missing = Assert.IsType<FhirMissingFieldExpression>(result);
            Assert.Equal(FhirFieldName.TokenSystem, missing.FieldName);
            Assert.Equal(2, missing.ComponentIndex);
        }

        public static IEnumerable<object[]> BinaryValueCases()
        {
            yield return new object[] { IgnixaFieldName.Number, 5m };
            yield return new object[] { IgnixaFieldName.Quantity, 5.4m };
            yield return new object[] { IgnixaFieldName.DateTimeStart, DateTimeOffset.Parse("2020-01-02T03:04:05Z") };
            yield return new object[] { IgnixaFieldName.DateTimeEnd, DateTimeOffset.Parse("2020-12-31T23:59:59Z") };
        }

        [Theory]
        [MemberData(nameof(BinaryValueCases))]
        public void GivenBinaryFieldValue_WhenConverted_ThenOperatorFieldAndValuePreserved(IgnixaFieldName ignixaFieldName, object value)
        {
            IgnixaExpr input = IgnixaExpr.GreaterThanOrEqual(ignixaFieldName, null, value);

            FhirExpression result = CreateBridge().Convert(input);

            var binary = Assert.IsType<FhirBinaryExpression>(result);
            Assert.Equal(FhirBinaryOperator.GreaterThanOrEqual, binary.BinaryOperator);
            Assert.Equal(value, binary.Value);
        }

        [Fact]
        public void GivenReferenceAndTokenAndUriStringValues_WhenConverted_ThenFieldNamesPreserved()
        {
            IgnixaExpr referenceInput = IgnixaExpr.StringEquals(IgnixaFieldName.ReferenceResourceId, null, "123", false);
            IgnixaExpr tokenInput = IgnixaExpr.StringEquals(IgnixaFieldName.TokenSystem, null, "http://loinc.org", false);
            IgnixaExpr uriInput = IgnixaExpr.StringEquals(IgnixaFieldName.Uri, null, "http://example.org/x", false);

            IIgnixaLegacyExpressionBridge bridge = CreateBridge();

            Assert.Equal(FhirFieldName.ReferenceResourceId, Assert.IsType<FhirStringExpression>(bridge.Convert(referenceInput)).FieldName);
            Assert.Equal(FhirFieldName.TokenSystem, Assert.IsType<FhirStringExpression>(bridge.Convert(tokenInput)).FieldName);
            Assert.Equal(FhirFieldName.Uri, Assert.IsType<FhirStringExpression>(bridge.Convert(uriInput)).FieldName);
        }

        [Fact]
        public void GivenCompositeComponentIndex_WhenConverted_ThenComponentIndexPreserved()
        {
            IgnixaExpr input = IgnixaExpr.Equals(IgnixaFieldName.Quantity, 1, 9.9m);

            FhirExpression result = CreateBridge().Convert(input);

            var binary = Assert.IsType<FhirBinaryExpression>(result);
            Assert.Equal(1, binary.ComponentIndex);
            Assert.Equal(9.9m, binary.Value);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GivenChainedExpression_WhenConverted_ThenChainDirectionAndTargetsPreserved(bool reversed)
        {
            IgnixaExpr input = IgnixaExpr.Chained(
                new[] { "Observation" },
                _subjectIgnixaParameter,
                new[] { "Patient" },
                reversed,
                IgnixaExpr.SearchParameter(_nameIgnixaParameter, IgnixaExpr.StringEquals(IgnixaFieldName.String, null, "Smith", false)));

            FhirExpression result = CreateBridge().Convert(input);

            var chained = Assert.IsType<FhirChainedExpression>(result);
            Assert.Equal(new[] { "Observation" }, chained.ResourceTypes);
            Assert.Same(_subjectParameter, chained.ReferenceSearchParameter);
            Assert.Equal(new[] { "Patient" }, chained.TargetResourceTypes);
            Assert.Equal(reversed, chained.Reversed);
            Assert.IsType<FhirSearchParameterExpression>(chained.Expression);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void GivenIncludeExpression_WhenConverted_ThenModeReverseAndIteratePreserved(bool reversed, bool iterate)
        {
            IgnixaExpr input = IgnixaExpr.Include(
                new[] { "Patient" },
                _generalPractitionerIgnixaParameter,
                "Patient",
                "Practitioner",
                new[] { "Practitioner" },
                wildCard: false,
                reversed: reversed,
                iterate: iterate);

            FhirExpression result = CreateBridge().Convert(input);

            var include = Assert.IsType<FhirIncludeExpression>(result);
            Assert.Same(_generalPractitionerParameter, include.ReferenceSearchParameter);
            Assert.Equal("Patient", include.SourceResourceType);
            Assert.Equal("Practitioner", include.TargetResourceType);
            Assert.False(include.WildCard);
            Assert.Equal(reversed, include.Reversed);
            Assert.Equal(iterate, include.Iterate);
            Assert.Equal(new[] { "Practitioner" }, include.ReferencedTypes);
        }

        [Fact]
        public void GivenWildcardInclude_WhenConverted_ThenReferenceParameterIsNullAndWildcardSet()
        {
            IgnixaExpr input = IgnixaExpr.Include(
                new[] { "Patient" },
                referenceSearchParameter: null,
                sourceResourceType: "Patient",
                targetResourceType: null,
                referencedTypes: null,
                wildCard: true,
                reversed: false,
                iterate: false);

            FhirExpression result = CreateBridge().Convert(input);

            var include = Assert.IsType<FhirIncludeExpression>(result);
            Assert.True(include.WildCard);
            Assert.Null(include.ReferenceSearchParameter);
        }

        [Fact]
        public void GivenUnionExpression_WhenConverted_ThenOperatorAndOperandsPreserved()
        {
            IgnixaExpr input = IgnixaExpr.Union(
                IgnixaUnionOperator.All,
                new IgnixaExpr[]
                {
                    IgnixaExpr.StringEquals(IgnixaFieldName.TokenCode, null, "a", false),
                    IgnixaExpr.StringEquals(IgnixaFieldName.TokenCode, null, "b", false),
                });

            FhirExpression result = CreateBridge().Convert(input);

            var union = Assert.IsType<FhirUnionExpression>(result);
            Assert.Equal(2, union.Expressions.Count);
            Assert.Equal("a", Assert.IsType<FhirStringExpression>(union.Expressions[0]).Value);
            Assert.Equal("b", Assert.IsType<FhirStringExpression>(union.Expressions[1]).Value);
        }

        [Fact]
        public void GivenInExpression_WhenConverted_ThenFieldAndValuesPreserved()
        {
            IgnixaExpr input = IgnixaExpr.In(IgnixaFieldName.TokenCode, null, new[] { "a", "b", "c" });

            FhirExpression result = CreateBridge().Convert(input);

            var inExpression = Assert.IsType<FhirInExpression>(result);
            Assert.Equal(FhirFieldName.TokenCode, inExpression.FieldName);
            Assert.Equal(new[] { "a", "b", "c" }, inExpression.Values);
        }

        [Fact]
        public void GivenCompartmentSearch_WhenConverted_ThenCompartmentPreserved()
        {
            var compartment = new global::Ignixa.Search.Expressions.CompartmentSearchExpression(
                "Patient",
                "123",
                new HashSet<string> { "Observation" });

            FhirExpression result = CreateBridge().Convert(compartment);

            var fhirCompartment = Assert.IsType<FhirCompartmentSearchExpression>(result);
            Assert.Equal("Patient", fhirCompartment.CompartmentType);
            Assert.Equal("123", fhirCompartment.CompartmentId);
            Assert.Equal(new[] { "Observation" }, fhirCompartment.FilteredResourceTypes);
        }

        [Fact]
        public void GivenFullWildcardNotReferenced_WhenConverted_ThenWildcardPreserved()
        {
            var notReferenced = new global::Ignixa.Search.Expressions.NotReferencedExpression(sourceResourceType: null, referencePath: null);

            FhirExpression result = CreateBridge().Convert(notReferenced);

            var fhirNotReferenced = Assert.IsType<FhirNotReferencedExpression>(result);
            Assert.True(fhirNotReferenced.WildCard);
        }

        [Fact]
        public void GivenNonWildcardNotReferenced_WhenConverted_ThenBridgeExceptionCarriesMetadata()
        {
            var notReferenced = new global::Ignixa.Search.Expressions.NotReferencedExpression(sourceResourceType: "Patient", referencePath: "Patient.link");

            var exception = Assert.Throws<IgnixaExpressionBridgeException>(() => CreateBridge().Convert(notReferenced));
            Assert.Equal(nameof(global::Ignixa.Search.Expressions.NotReferencedExpression), exception.NodeType);
            Assert.Equal("Patient.link", exception.ParameterCode);
            Assert.NotNull(exception.Reason);
        }

        [Fact]
        public void GivenUnsupportedFieldName_WhenConverted_ThenBridgeExceptionCarriesParameterCode()
        {
            IgnixaExpr input = IgnixaExpr.SearchParameter(
                CreateIgnixaParameter("url", "http://hl7.org/fhir/SearchParameter/ValueSet-url"),
                IgnixaExpr.StringEquals(IgnixaFieldName.UriVersion, null, "1.0", false));

            ISearchParameterDefinitionManager definitionManager = CreateDefinitionManager(
                ("http://hl7.org/fhir/SearchParameter/ValueSet-url", new FhirSpi("url", "url", global::Microsoft.Health.Fhir.ValueSets.SearchParamType.Uri, new Uri("http://hl7.org/fhir/SearchParameter/ValueSet-url"))));

            var exception = Assert.Throws<IgnixaExpressionBridgeException>(() => new IgnixaLegacyExpressionBridge(definitionManager).Convert(input));
            Assert.Equal("url", exception.ParameterCode);
        }

        [Fact]
        public void GivenUnresolvableSearchParameter_WhenConverted_ThenBridgeExceptionCarriesNodeMetadata()
        {
            IgnixaExpr input = IgnixaExpr.SearchParameter(
                CreateIgnixaParameter("missing", "http://example.org/SearchParameter/missing"),
                IgnixaExpr.StringEquals(IgnixaFieldName.String, null, "x", false));

            // The definition manager resolves no parameters, so the URL lookup fails.
            ISearchParameterDefinitionManager definitionManager = CreateDefinitionManager();

            var exception = Assert.Throws<IgnixaExpressionBridgeException>(() => new IgnixaLegacyExpressionBridge(definitionManager).Convert(input));
            Assert.Equal(nameof(global::Ignixa.Search.Expressions.SearchParameterExpression), exception.NodeType);
            Assert.Equal("missing", exception.ParameterCode);
        }

        [Fact]
        public async Task GivenCanonicalIgnixaExpression_WhenLoweredAndBridged_ThenProducesFhirSearchParameterTree()
        {
            // Build a canonical Ignixa expression from a real parse of a real query.
            IgnixaSearchOptionsAdapter adapter = CreateRealAdapter();
            global::Ignixa.Search.Models.SearchOptions ignixaOptions = adapter.Build("Patient", new[] { Tuple.Create("name", "Smith") }, null);
            Assert.NotNull(ignixaOptions.Expression);

            SearchParameterDefinitionManager definitionManager = await new SearchParameterFixtureData().GetSearchDefinitionManagerAsync();
            var bridge = new IgnixaLegacyExpressionBridge(definitionManager);

            // Lower the canonical expression and bridge it to the FHIR Server model.
            global::Ignixa.Search.Expressions.Expression lowered = global::Ignixa.Search.Expressions.LegacyExpressionLowerer.LowerToLegacy(ignixaOptions.Expression);
            FhirExpression bridged = bridge.Convert(lowered);

            var bridgedParameter = Assert.IsType<FhirSearchParameterExpression>(bridged);
            Assert.Equal("name", bridgedParameter.Parameter.Code);
            Assert.NotNull(bridgedParameter.Expression);
        }

        [Fact]
        public async Task GivenSameFixture_WhenLoweredAndBridged_ThenShapeMatchesLegacyParserOracle()
        {
            IgnixaSearchOptionsAdapter adapter = CreateRealAdapter();
            global::Ignixa.Search.Models.SearchOptions ignixaOptions = adapter.Build("Patient", new[] { Tuple.Create("name", "Smith") }, null);

            SearchParameterDefinitionManager definitionManager = await new SearchParameterFixtureData().GetSearchDefinitionManagerAsync();
            var bridge = new IgnixaLegacyExpressionBridge(definitionManager);
            global::Ignixa.Search.Expressions.Expression lowered = global::Ignixa.Search.Expressions.LegacyExpressionLowerer.LowerToLegacy(ignixaOptions.Expression);
            FhirExpression bridged = bridge.Convert(lowered);

            // Legacy parser oracle over the same fixture.
            var searchParameterExpressionParser = new SearchParameterExpressionParser(Substitute.For<IReferenceSearchValueParser>());
            var oracleParser = new ExpressionParser(() => definitionManager, searchParameterExpressionParser);
            FhirExpression oracle = oracleParser.Parse(new[] { "Patient" }, "name", "Smith");

            // Both must produce a search-parameter expression over the same parameter with a string predicate.
            var bridgedParameter = Assert.IsType<FhirSearchParameterExpression>(bridged);
            var oracleParameter = Assert.IsType<FhirSearchParameterExpression>(oracle);
            Assert.Equal(oracleParameter.Parameter.Code, bridgedParameter.Parameter.Code);

            var bridgedString = Assert.IsType<FhirStringExpression>(bridgedParameter.Expression);
            var oracleString = Assert.IsType<FhirStringExpression>(oracleParameter.Expression);
            Assert.Equal(oracleString.FieldName, bridgedString.FieldName);
            Assert.Equal(oracleString.StringOperator, bridgedString.StringOperator);
            Assert.Equal(oracleString.IgnoreCase, bridgedString.IgnoreCase);
            Assert.Equal(oracleString.Value, bridgedString.Value, ignoreCase: true);
        }

        [Fact]
        public void GivenBridgeException_WhenInspected_ThenItIsASearchOperationNotSupportedException()
        {
            var notReferenced = new global::Ignixa.Search.Expressions.NotReferencedExpression(sourceResourceType: "Patient", referencePath: "Patient.link");

            Exception exception = Assert.Throws<IgnixaExpressionBridgeException>(() => CreateBridge().Convert(notReferenced));
            Assert.IsAssignableFrom<Microsoft.Health.Fhir.Core.Features.Search.SearchOperationNotSupportedException>(exception);
        }

        private static IgnixaSpi CreateIgnixaParameter(string code, string url)
        {
            return new IgnixaSpi(
                code,
                code,
                global::Ignixa.Specification.ValueSets.Normative.SearchParamType.String,
                new Uri(url),
                components: null,
                expression: null,
                targetResourceTypes: null,
                baseResourceTypes: null,
                description: null);
        }

        private IIgnixaLegacyExpressionBridge CreateBridge()
        {
            return new IgnixaLegacyExpressionBridge(CreateDefinitionManager(
                (NameUrl, _nameParameter),
                (SubjectUrl, _subjectParameter),
                (GeneralPractitionerUrl, _generalPractitionerParameter)));
        }

        private static ISearchParameterDefinitionManager CreateDefinitionManager(params (string Url, FhirSpi Parameter)[] parameters)
        {
            var map = parameters.ToDictionary(p => p.Url, p => p.Parameter, StringComparer.Ordinal);

            ISearchParameterDefinitionManager definitionManager = Substitute.For<ISearchParameterDefinitionManager>();
            definitionManager
                .TryGetSearchParameter(Arg.Any<string>(), out Arg.Any<FhirSpi>())
                .Returns(callInfo =>
                {
                    string url = callInfo.ArgAt<string>(0);
                    if (map.TryGetValue(url, out FhirSpi parameter))
                    {
                        callInfo[1] = parameter;
                        return true;
                    }

                    callInfo[1] = null;
                    return false;
                });

            return definitionManager;
        }

        private static IgnixaSearchOptionsAdapter CreateRealAdapter()
        {
            global::Ignixa.Abstractions.IFhirSchemaProvider schemaProvider = IgnixaFhirVersionAdapter.Current.GetSchemaProvider();
            var referenceSearchValueParser = new global::Ignixa.Search.Indexing.SearchValues.ReferenceSearchValueParser(schemaProvider);
            var searchParameterExpressionParser = new global::Ignixa.Search.Expressions.Parsers.SearchParameterExpressionParser(referenceSearchValueParser, schemaProvider);
            var searchParameterDefinitionManager = new global::Ignixa.Search.Definition.SearchParameterDefinitionManager(
                schemaProvider,
                NullLogger<global::Ignixa.Search.Definition.SearchParameterDefinitionManager>.Instance);
            var searchableSearchParameterDefinitionManager = new global::Ignixa.Search.Definition.SearchableSearchParameterDefinitionManager(searchParameterDefinitionManager);
            global::Ignixa.Search.Definition.ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver resolver = () => searchableSearchParameterDefinitionManager;
            var expressionParser = new global::Ignixa.Search.Expressions.Parsers.ExpressionParser(
                resolver,
                searchParameterExpressionParser,
                schemaProvider);
            var builderFactory = new IgnixaSearchOptionsBuilderFactory(expressionParser, searchableSearchParameterDefinitionManager);

            return new IgnixaSearchOptionsAdapter(builderFactory, schemaProvider);
        }
    }
}
