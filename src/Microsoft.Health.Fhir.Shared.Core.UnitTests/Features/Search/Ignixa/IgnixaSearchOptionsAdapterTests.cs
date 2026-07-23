// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Ignixa.Abstractions;
using Ignixa.Search.Parsing;
using Ignixa.Specification.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Core.UnitTests.Features.Context;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search.Ignixa
{
    [Trait(Traits.OwningTeam, Microsoft.Health.Fhir.Tests.Common.OwningTeam.Fhir)]
    [Trait(Traits.Category, Microsoft.Health.Fhir.Tests.Common.Categories.Search)]
    public class IgnixaSearchOptionsAdapterTests
    {
        [Fact]
        public void GivenCompiledFhirVersion_WhenCurrentVersionIsRequested_ThenExpectedIgnixaVersionIsReturned()
        {
#if Stu3
            const FhirVersion expectedVersion = FhirVersion.Stu3;
#elif R4B
            const FhirVersion expectedVersion = FhirVersion.R4B;
#elif R4
            const FhirVersion expectedVersion = FhirVersion.R4;
#elif R5
            const FhirVersion expectedVersion = FhirVersion.R5;
#else
#error No FHIR version compilation symbol is configured.
#endif

            Assert.Equal(expectedVersion, IgnixaFhirVersionAdapter.Current);
        }

        [Fact]
        public void GivenDecodedQueryParameters_WhenBuildIsCalled_ThenAdapterPreservesOrderAndTenant()
        {
            var builderFactory = new RecordingSearchOptionsBuilderFactory();
            var schemaProvider = Substitute.For<IFhirSchemaProvider>();
            var adapter = new IgnixaSearchOptionsAdapter(builderFactory, schemaProvider);
            var queryParameters = new[]
            {
                Tuple.Create("name", "Smith"),
                Tuple.Create("name", "Jones"),
                Tuple.Create("identifier", "http://example.org|123"),
            };

            global::Ignixa.Search.Models.SearchOptions result = adapter.Build("Patient", queryParameters, 42);

            Assert.Same(builderFactory.Builder.Options, result);
            Assert.Equal(IgnixaFhirVersionAdapter.Current, builderFactory.Version);
            Assert.Equal(42, builderFactory.TenantId);
            Assert.Same(schemaProvider, builderFactory.Builder.Schema);
            Assert.Equal("Patient", builderFactory.Builder.ResourceType);
            Assert.Collection(
                builderFactory.Builder.QueryParameters,
                p =>
                {
                    Assert.Equal("name", p.Name);
                    Assert.Equal("Smith", p.Value);
                },
                p =>
                {
                    Assert.Equal("name", p.Name);
                    Assert.Equal("Jones", p.Value);
                },
                p =>
                {
                    Assert.Equal("identifier", p.Name);
                    Assert.Equal("http://example.org|123", p.Value);
                });
        }

        [Theory]
        [InlineData(null, "value")]
        [InlineData("name", null)]
        public void GivenNullQueryParameterNameOrValue_WhenBuildIsCalled_ThenExceptionIsThrownBeforeIgnixa(string name, string value)
        {
            var builderFactory = new RecordingSearchOptionsBuilderFactory();
            var schemaProvider = Substitute.For<IFhirSchemaProvider>();
            var adapter = new IgnixaSearchOptionsAdapter(builderFactory, schemaProvider);

            Assert.Throws<ArgumentException>(() => adapter.Build("Patient", new[] { Tuple.Create(name, value) }, null));
            Assert.Null(builderFactory.Builder.QueryParameters);
        }

        [Fact]
        public void GivenCountTotalAndSummaryControls_WhenRealAdapterBuilds_ThenIgnixaOptionsReflectControls()
        {
            IgnixaSearchOptionsAdapter adapter = CreateRealAdapter();

            global::Ignixa.Search.Models.SearchOptions options = adapter.Build(
                "Patient",
                new[]
                {
                    Tuple.Create("_count", "3"),
                    Tuple.Create("_total", "accurate"),
                    Tuple.Create("_summary", "count"),
                },
                null);

            Assert.Equal(3, options.MaxItemCount);
            Assert.Equal(global::Ignixa.Search.Models.TotalType.Accurate, options.Total);
            Assert.Equal(global::Ignixa.Search.Models.SummaryType.Count, options.Summary);
        }

        [Fact]
        public void GivenIncludeAndRepeatedParameters_WhenRealAdapterBuilds_ThenIgnixaPreservesIncludeAndExpression()
        {
            IgnixaSearchOptionsAdapter adapter = CreateRealAdapter();

            global::Ignixa.Search.Models.SearchOptions options = adapter.Build(
                "Observation",
                new[]
                {
                    Tuple.Create("_include", "Observation:subject"),
                    Tuple.Create("code", "http://loinc.org|8480-6"),
                    Tuple.Create("code", "http://loinc.org|8462-4"),
                },
                null);

            Assert.NotNull(options.Expression);
            Assert.NotEmpty(options.Include);
        }

        [Theory]
        [InlineData("Patient", "name", "Smith")]
        [InlineData("Patient", "_tag", "http://example.org|code")]
        [InlineData("Observation", "value-quantity", "ge5.4|http://unitsofmeasure.org|mg")]
        [InlineData("Observation", "subject", "Patient/123")]
#if !Stu3
        [InlineData("Observation", "code-value-concept", "http://loinc.org|8480-6$http://snomed.info/sct|123")]
#endif
        [InlineData("ValueSet", "url:below", "http://example.org/fhir/ValueSet")]
        public void GivenOrdinarySearchParameterShape_WhenRealAdapterBuilds_ThenIgnixaExpressionIsCreated(string resourceType, string name, string value)
        {
            IgnixaSearchOptionsAdapter adapter = CreateRealAdapter();

            global::Ignixa.Search.Models.SearchOptions options = adapter.Build(
                resourceType,
                new[] { Tuple.Create(name, value) },
                null);

            Assert.NotNull(options.Expression);
        }

        [Fact]
        public void GivenChainSearchParameter_WhenRealAdapterBuilds_ThenIgnixaReportsUnsupportedParameter()
        {
            IgnixaSearchOptionsAdapter adapter = CreateRealAdapter();

            global::Ignixa.Search.Models.SearchOptions options = adapter.Build(
                "Observation",
                new[] { Tuple.Create("subject.name", "Smith") },
                null);

            Assert.Contains("subject.name", options.UnsupportedParams);
        }

        [Theory]
        [InlineData("name", "Smith")]
        [InlineData("subject.name", "Smith")]
        public void GivenSameSearchInput_WhenComparedWithLegacyParser_ThenSupportOutcomeMatches(string name, string value)
        {
            IgnixaSearchOptionsAdapter adapter = CreateRealAdapter();
            global::Ignixa.Search.Models.SearchOptions ignixaOptions = adapter.Build(
                "Patient",
                new[] { Tuple.Create(name, value) },
                null);

            ISearchParameterDefinitionManager searchParameterDefinitionManager = Substitute.For<ISearchParameterDefinitionManager>();
            var nameSearchParameter = new SearchParameterInfo("name", "name", SearchParamType.String);
            searchParameterDefinitionManager.GetSearchParameter(Arg.Any<string>(), Arg.Any<string>())
                .Returns(callInfo =>
                {
                    if (callInfo.ArgAt<string>(0) == "Patient" && callInfo.ArgAt<string>(1) == "name")
                    {
                        return nameSearchParameter;
                    }

                    throw new SearchParameterNotSupportedException(callInfo.ArgAt<string>(0), callInfo.ArgAt<string>(1));
                });

            var searchParameterExpressionParser = Substitute.For<ISearchParameterExpressionParser>();
            searchParameterExpressionParser.Parse(
                    Arg.Any<SearchParameterInfo>(),
                    Arg.Any<SearchModifier>(),
                    Arg.Any<string>())
                .Returns(Expression.StringEquals(FieldName.String, null, value, true));

            var legacyParser = new Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers.ExpressionParser(
                () => searchParameterDefinitionManager,
                searchParameterExpressionParser);

            bool legacySupported;
            try
            {
                Assert.NotNull(legacyParser.Parse(new[] { "Patient" }, name, value));
                legacySupported = true;
            }
            catch (SearchParameterNotSupportedException)
            {
                legacySupported = false;
            }

            bool ignixaSupported = !ignixaOptions.UnsupportedParams.Contains(name);

            Assert.Equal(legacySupported, ignixaSupported);
            Assert.Equal(legacySupported, ignixaOptions.Expression != null);
        }

        [Fact]
        public void GivenNoTenantContextProperty_WhenTenantIdIsRequested_ThenNullIsReturned()
        {
            var requestContext = new DefaultFhirRequestContext();
            IgnixaSearchTenantAccessor accessor = CreateTenantAccessor(requestContext);

            Assert.Null(accessor.TenantId);
        }

        [Fact]
        public void GivenIntegerTenantContextProperty_WhenTenantIdIsRequested_ThenTenantIdIsReturned()
        {
            var requestContext = new DefaultFhirRequestContext();
            requestContext.Properties[global::Microsoft.Health.Fhir.Core.Features.Search.IgnixaSearchContextPropertyNames.TenantId] = 123;
            IgnixaSearchTenantAccessor accessor = CreateTenantAccessor(requestContext);

            Assert.Equal(123, accessor.TenantId);
        }

        [Fact]
        public void GivenNonIntegerTenantContextProperty_WhenTenantIdIsRequested_ThenInvalidOperationExceptionIsThrown()
        {
            var requestContext = new DefaultFhirRequestContext();
            requestContext.Properties[global::Microsoft.Health.Fhir.Core.Features.Search.IgnixaSearchContextPropertyNames.TenantId] = "123";
            IgnixaSearchTenantAccessor accessor = CreateTenantAccessor(requestContext);

            Assert.Throws<InvalidOperationException>(() => accessor.TenantId);
        }

        private static IgnixaSearchTenantAccessor CreateTenantAccessor(IFhirRequestContext requestContext)
        {
            RequestContextAccessor<IFhirRequestContext> contextAccessor = requestContext.SetupAccessor();
            return new IgnixaSearchTenantAccessor(contextAccessor);
        }

        private static IgnixaSearchOptionsAdapter CreateRealAdapter()
        {
            IFhirSchemaProvider schemaProvider = IgnixaFhirVersionAdapter.Current.GetSchemaProvider();
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

        private sealed class RecordingSearchOptionsBuilderFactory : ISearchOptionsBuilderFactory
        {
            public RecordingSearchOptionsBuilder Builder { get; } = new RecordingSearchOptionsBuilder();

            public FhirVersion? Version { get; private set; }

            public int? TenantId { get; private set; }

            public ISearchOptionsBuilder Create(FhirVersion fhirVersion)
            {
                Version = fhirVersion;
                TenantId = null;
                return Builder;
            }

            public ISearchOptionsBuilder Create(FhirVersion fhirVersion, int? tenantId)
            {
                Version = fhirVersion;
                TenantId = tenantId;
                return Builder;
            }
        }

        private sealed class RecordingSearchOptionsBuilder : ISearchOptionsBuilder
        {
            public global::Ignixa.Search.Models.SearchOptions Options { get; } = new global::Ignixa.Search.Models.SearchOptions();

            public string ResourceType { get; private set; }

            public IReadOnlyList<QueryParameter> QueryParameters { get; private set; }

            public ISchema Schema { get; private set; }

            public IList<ParameterTrace> ParameterTrace { get; private set; }

            public global::Ignixa.Search.Models.SearchOptions Build(string resourceType, IReadOnlyList<QueryParameter> queryParameters, ISchema schema, IList<ParameterTrace> parameterTrace)
            {
                ResourceType = resourceType;
                QueryParameters = queryParameters;
                Schema = schema;
                ParameterTrace = parameterTrace;
                return Options;
            }
        }
    }
}
