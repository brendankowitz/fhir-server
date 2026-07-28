// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Access;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Core.UnitTests.Features.Context;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Microsoft.Health.Fhir.Core.UnitTests.Features.Search.SearchExpressionTestHelper;
using SortOrder = Microsoft.Health.Fhir.Core.Features.Search.SortOrder;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search
{
    /// <summary>
    /// Test class for SearchOptionsFactory.Create
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    public partial class SearchOptionsFactoryTests
    {
        private const string DefaultResourceType = "Patient";
        private const string ContinuationTokenParamName = "ct";

        private readonly IExpressionParser _expressionParser = Substitute.For<IExpressionParser>();
        private readonly SearchOptionsFactory _factory;
        private readonly SearchParameterInfo _resourceTypeSearchParameterInfo;
        private readonly SearchParameterInfo _lastUpdatedSearchParameterInfo;
        private readonly CoreFeatureConfiguration _coreFeatures;
        private DefaultFhirRequestContext _defaultFhirRequestContext;
        private readonly ISortingValidator _sortingValidator;
        private readonly IIgnixaSearchOptionsAdapter _ignixaAdapter;
        private readonly IgnixaSearchTenantAccessor _ignixaSearchTenantAccessor;
        private readonly ISearchParameterDefinitionManager _searchParameterDefinitionManager;

        public SearchOptionsFactoryTests()
        {
            ISearchParameterDefinitionManager searchParameterDefinitionManager = Substitute.For<ISearchParameterDefinitionManager>();
            _searchParameterDefinitionManager = searchParameterDefinitionManager;
            _resourceTypeSearchParameterInfo = new SearchParameter { Name = SearchParameterNames.ResourceType, Code = SearchParameterNames.ResourceType, Type = SearchParamType.String, Url = SearchParameterNames.ResourceTypeUri.AbsoluteUri }.ToInfo();
            _lastUpdatedSearchParameterInfo = new SearchParameter { Name = SearchParameterNames.LastUpdated, Code = SearchParameterNames.LastUpdated, Type = SearchParamType.String }.ToInfo();
            searchParameterDefinitionManager.GetSearchParameter(Arg.Any<string>(), Arg.Any<string>()).Throws(ci => new SearchParameterNotSupportedException(ci.ArgAt<string>(0), ci.ArgAt<string>(1)));
            searchParameterDefinitionManager.GetSearchParameter(Arg.Any<string>(), SearchParameterNames.ResourceType).Returns(_resourceTypeSearchParameterInfo);
            searchParameterDefinitionManager.GetSearchParameter(Arg.Any<string>(), SearchParameterNames.LastUpdated).Returns(_lastUpdatedSearchParameterInfo);
            _coreFeatures = new CoreFeatureConfiguration();
            _defaultFhirRequestContext = new DefaultFhirRequestContext();

            _sortingValidator = Substitute.For<ISortingValidator>();
            _ignixaAdapter = Substitute.For<IIgnixaSearchOptionsAdapter>();
            _ignixaAdapter.Build(Arg.Any<string>(), Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<int?>())
                .Returns(new global::Ignixa.Search.Models.SearchOptions());

            RequestContextAccessor<IFhirRequestContext> contextAccessor = _defaultFhirRequestContext.SetupAccessor();
            _ignixaSearchTenantAccessor = new IgnixaSearchTenantAccessor(contextAccessor);
            _factory = new SearchOptionsFactory(
                _expressionParser,
                () => searchParameterDefinitionManager,
                new OptionsWrapper<CoreFeatureConfiguration>(_coreFeatures),
                contextAccessor,
                _sortingValidator,
                new ExpressionAccessControl(contextAccessor),
                _ignixaAdapter,
                _ignixaSearchTenantAccessor,
                NullLogger<SearchOptionsFactory>.Instance);
        }

        public static IEnumerable<object[]> GetSearchParameterTestData
        {
            get
            {
                yield return new object[]
                {
                    "Patient",
                    new List<ScopeRestriction>
                    {
                        new ScopeRestriction("Patient", DataActions.Read, "patient", new SearchParams("code1", "foo")),
                        new ScopeRestriction("Observation", DataActions.Read, "patient", new SearchParams("code2", "doo")),
                    },
                    new List<Tuple<string, string>>(),
                    "(And (Param ResourceType (StringEquals TokenCode 'Patient')) (Union (All) [(And (And (Param ResourceType (StringEquals TokenCode 'Patient')) code1=foo))]))",
                };
                yield return new object[]
                {
                    "Patient",
                    new List<ScopeRestriction>
                    {
                        new ScopeRestriction("Patient", DataActions.Read, "patient", CreateSearchParams(("code1", "foo"), ("code2", "goo"))),
                        new ScopeRestriction("Observation", DataActions.Read, "patient", CreateSearchParams(("code2", "doo"))),
                    },
                    new List<Tuple<string, string>>
                    {
                        Tuple.Create("_type", "Patient,Observation,Practitioner"),
                        Tuple.Create("tag", "xyz"),
                    },
                    "(And (Param ResourceType (StringEquals TokenCode 'Patient')) (Union (All) [(And (And (Param ResourceType (StringEquals TokenCode 'Patient')) code1=foo) (And (Param ResourceType (StringEquals TokenCode 'Patient')) code2=goo))]) _type=Patient,Observation,Practitioner tag=xyz)",
                };
                yield return new object[]
                {
                    "Patient",
                    new List<ScopeRestriction>
                    {
                        new ScopeRestriction("Patient", DataActions.Read, "patient", CreateSearchParams(("code1", "foo"), ("code2", "goo"))),
                        new ScopeRestriction("Observation", DataActions.Read, "patient", CreateSearchParams(("code2", "doo"))),
                    },
                    new List<Tuple<string, string>>
                    {
                        Tuple.Create("tag", "xyz"),
                    },
                    "(And (Param ResourceType (StringEquals TokenCode 'Patient')) (Union (All) [(And (And (Param ResourceType (StringEquals TokenCode 'Patient')) code1=foo) (And (Param ResourceType (StringEquals TokenCode 'Patient')) code2=goo))]) tag=xyz)",
                };
                yield return new object[]
                {
                    "Patient",
                    new List<ScopeRestriction>
                    {
                        new ScopeRestriction("all", DataActions.Read, "patient", CreateSearchParams(("_type", "Practitioner,CarePlan,Organization"))),
                        new ScopeRestriction("Observation", DataActions.Read, "patient", CreateSearchParams(("code2", "doo"))),
                    },
                    null,
                    "(And (Param ResourceType (StringEquals TokenCode 'Patient')) _type=Practitioner,CarePlan,Organization)",
                };
                yield return new object[]
                {
                    "Patient",
                    new List<ScopeRestriction>
                    {
                        new ScopeRestriction("all", DataActions.Search, "patient", null),
                        new ScopeRestriction("Observation", DataActions.Search, "patient", CreateSearchParams(("code2", "doo"))),
                    },
                    null,
                    "(Param ResourceType (StringEquals TokenCode 'Patient'))",
                };
                yield return new object[]
                {
                    "Patient",
                    new List<ScopeRestriction>
                    {
                        new ScopeRestriction("all", DataActions.Search, "patient", CreateSearchParams(("_type", "Observation"))),
                        new ScopeRestriction("Observation", DataActions.Search, "patient", CreateSearchParams(("code2", "doo"))),
                    },
                    null,
                    "(And (Param ResourceType (StringEquals TokenCode 'Patient')) _type=Observation)",
                };
                yield return new object[]
                {
                    null,
                    new List<ScopeRestriction>
                    {
                        new ScopeRestriction("all", DataActions.Search, "patient", null),
                    },
                    null,
                    null,
                };
                yield return new object[]
                {
                    null,
                    new List<ScopeRestriction>
                    {
                        new ScopeRestriction("Observation", DataActions.Search, "patient", CreateSearchParams(("code1", "doo"))),
                        new ScopeRestriction("Encounter", DataActions.Search, "patient", CreateSearchParams(("code2", "goo"))),
                    },
                    null,
                    "(Union (All) [(And (And (Param ResourceType (StringEquals TokenCode 'Observation')) code1=doo)) OR (And (And (Param ResourceType (StringEquals TokenCode 'Encounter')) code2=goo))])",
                };
            }
        }

        private static SearchParams CreateSearchParams(params (string key, string value)[] items)
        {
            var searchParams = new SearchParams();
            foreach (var item in items)
            {
                searchParams.Add(item.key, item.value);
            }

            return searchParams;
        }

        [Fact]
        public void GivenANullQueryParameters_WhenCreated_ThenDefaultSearchOptionsShouldBeCreated()
        {
            SearchOptions options = CreateSearchOptions(queryParameters: null);

            Assert.NotNull(options);

            Assert.Null(options.ContinuationToken);
            Assert.Equal(_coreFeatures.DefaultItemCountPerSearch, options.MaxItemCount);
            ValidateResourceTypeSearchParameterExpression(options.Expression, DefaultResourceType);
        }

        [Fact]
        public void GivenMultipleContinuationTokens_WhenCreated_ThenExceptionShouldBeThrown()
        {
            const string encodedContinuationToken = "MTIz";

            Assert.Throws<InvalidSearchOperationException>(() => CreateSearchOptions(
                queryParameters: new[]
                {
                    Tuple.Create(ContinuationTokenParamName, encodedContinuationToken),
                    Tuple.Create(ContinuationTokenParamName, encodedContinuationToken),
                }));
        }

        [Fact]
        public void GivenACount_WhenCreated_ThenCorrectMaxItemCountShouldBeSet()
        {
            SearchOptions options = CreateSearchOptions(
                queryParameters: new[]
                {
                    Tuple.Create("_count", "5"),
                });

            Assert.NotNull(options);
            Assert.Equal(5, options.MaxItemCount);
        }

        [Fact]
        public void GivenSearchParameters_WhenCreated_ThenIgnixaAdapterReceivesCanonicalParametersAndTenant()
        {
            var ignixaOptions = new global::Ignixa.Search.Models.SearchOptions();
            _defaultFhirRequestContext.Properties[IgnixaSearchContextPropertyNames.TenantId] = 17;

            var queryParameters = new[]
            {
                Tuple.Create(KnownQueryParameterNames.ContinuationToken, ContinuationTokenEncoder.Encode("token")),
                Tuple.Create(KnownQueryParameterNames.FeedRange, "range"),
                Tuple.Create(KnownQueryParameterNames.Format, "json"),
                Tuple.Create(KnownQueryParameterNames.Pretty, "true"),
                Tuple.Create(KnownQueryParameterNames.Type, "Patient"),
                Tuple.Create(KnownQueryParameterNames.Count, "5"),
                Tuple.Create(KnownQueryParameterNames.Total, "none"),
                Tuple.Create("name", "Smith"),
            };

            var expectedIgnixaParameters = new[]
            {
                Tuple.Create(KnownQueryParameterNames.Type, "Patient"),
                Tuple.Create(KnownQueryParameterNames.Count, "5"),
                Tuple.Create(KnownQueryParameterNames.Total, "none"),
                Tuple.Create("name", "Smith"),
            };

            _ignixaAdapter.Build(
                DefaultResourceType,
                Arg.Is<IReadOnlyList<Tuple<string, string>>>(actual => actual.SequenceEqual(expectedIgnixaParameters)),
                17)
                .Returns(ignixaOptions);

            SearchOptions options = CreateSearchOptions(queryParameters: queryParameters);

            Assert.Same(ignixaOptions, options.IgnixaOptions);
            _ignixaAdapter.Received(1).Build(
                DefaultResourceType,
                Arg.Is<IReadOnlyList<Tuple<string, string>>>(actual => actual.SequenceEqual(expectedIgnixaParameters)),
                17);
        }

        [Fact]
        public void GivenACountWithValueZero_WhenCreated_ThenCorrectMaxItemCountShouldBeSet()
        {
            const ResourceType resourceType = ResourceType.Encounter;
            var queryParameters = new[]
            {
               Tuple.Create("_count", "0"),
            };

            SearchOptions options = CreateSearchOptions(
            resourceType: resourceType.ToString(),
            queryParameters: queryParameters);

            Assert.NotNull(options);
            Assert.True(options.CountOnly);
        }

        [Fact]
        public void GivenDuplicateSearchParameterWithSameValue_WhenCreated_ThenSearchParameterIsParsedOnce()
        {
            const string parameterName = "_tag";
            const string parameterValue = "system|code";

            _expressionParser.Parse(
                Arg.Is<string[]>(x => x.Length == 1 && x[0] == DefaultResourceType),
                parameterName,
                parameterValue)
                .Returns(new StubExpression($"{parameterName}={parameterValue}"));

            var queryParameters = new[]
            {
                Tuple.Create(parameterName, parameterValue),
                Tuple.Create(parameterName, parameterValue),
            };

            SearchOptions options = CreateSearchOptions(queryParameters: queryParameters);

            Assert.NotNull(options);
            _expressionParser.Received(1).Parse(
                Arg.Is<string[]>(x => x.Length == 1 && x[0] == DefaultResourceType),
                parameterName,
                parameterValue);
        }

        [Fact]
        public void GivenDuplicateSearchParameterNameWithDifferentValues_WhenCreated_ThenBothSearchParametersAreParsed()
        {
            const string parameterName = "_tag";
            const string firstParameterValue = "system|code1";
            const string secondParameterValue = "system|code2";

            _expressionParser.Parse(
                Arg.Is<string[]>(x => x.Length == 1 && x[0] == DefaultResourceType),
                parameterName,
                firstParameterValue)
                .Returns(new StubExpression($"{parameterName}={firstParameterValue}"));

            _expressionParser.Parse(
                Arg.Is<string[]>(x => x.Length == 1 && x[0] == DefaultResourceType),
                parameterName,
                secondParameterValue)
                .Returns(new StubExpression($"{parameterName}={secondParameterValue}"));

            var queryParameters = new[]
            {
                Tuple.Create(parameterName, firstParameterValue),
                Tuple.Create(parameterName, secondParameterValue),
            };

            SearchOptions options = CreateSearchOptions(queryParameters: queryParameters);

            Assert.NotNull(options);
            _expressionParser.Received(1).Parse(
                Arg.Is<string[]>(x => x.Length == 1 && x[0] == DefaultResourceType),
                parameterName,
                firstParameterValue);

            _expressionParser.Received(1).Parse(
                Arg.Is<string[]>(x => x.Length == 1 && x[0] == DefaultResourceType),
                parameterName,
                secondParameterValue);
        }

        [Theory]
        [InlineData("a")]
        [InlineData("1.1")]
        public void GivenACountWithInvalidValue_WhenCreated_ThenExceptionShouldBeThrown(string value)
        {
            const ResourceType resourceType = ResourceType.Encounter;
            var queryParameters = new[]
            {
               Tuple.Create("_count", value),
            };

            Assert.Throws<System.FormatException>(() => CreateSearchOptions(
            resourceType: resourceType.ToString(),
            queryParameters: queryParameters));
        }

        [Fact]
        public void GivenNoneOfTheSearchParamIsSupported_WhenCreated_ThenCorrectExpressionShouldBeGenerated()
        {
            const ResourceType resourceType = ResourceType.Patient;
            const string paramName1 = "address-city";
            const string value1 = "Seattle";

            _expressionParser.Parse(Arg.Is<string[]>(x => x.Length == 1 && x[0] == resourceType.ToString()), paramName1, value1).Returns(
                x => throw new SearchParameterNotSupportedException(typeof(Patient), paramName1));

            var queryParameters = new[]
            {
                Tuple.Create(paramName1, value1),
            };

            SearchOptions options = CreateSearchOptions(
                resourceType: resourceType.ToString(),
                queryParameters: queryParameters);

            Assert.NotNull(options);
            ValidateResourceTypeSearchParameterExpression(options.Expression, resourceType.ToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("    ")]
        public void GivenASearchParamWithEmptyValue_WhenCreated_ThenSearchParamShouldBeAddedToUnsupportedList(string value)
        {
            const ResourceType resourceType = ResourceType.Patient;
            const string paramName = "address-city";

            var queryParameters = new[]
            {
                Tuple.Create(paramName, value),
            };

            SearchOptions options = CreateSearchOptions(
                resourceType: resourceType.ToString(),
                queryParameters: queryParameters);

            Assert.NotNull(options);
            Assert.Equal(queryParameters, options.UnsupportedSearchParams);
        }

        [Fact]
        public void GivenASearchParameterWithEmptyKey_WhenCreated_ThenSearchParameterShouldBeAddedToUnsupportedList()
        {
            var queryParameters = new[]
            {
                Tuple.Create(string.Empty, "city"),
            };

            SearchOptions options = CreateSearchOptions(ResourceType.Patient.ToString(), queryParameters: queryParameters);
            Assert.NotNull(options);
            Assert.Equal(queryParameters.Take(1), options.UnsupportedSearchParams);
        }

        [Fact]
        public void GivenSearchParametersWithEmptyKey_WhenCreated_ThenSearchParameterShouldBeAddedToUnsupportedList()
        {
            var queryParameters = new[]
            {
                Tuple.Create("patient", "city"),
                Tuple.Create(string.Empty, "anotherCity"),
            };

            SearchOptions options = CreateSearchOptions(ResourceType.Patient.ToString(), queryParameters);
            Assert.NotNull(options);
            Assert.Single(options.UnsupportedSearchParams);
            Assert.Equal(queryParameters.Skip(1).Take(1), options.UnsupportedSearchParams);
        }

        [Fact]
        public void GivenSearchParametersWithEmptyKeyEmptyValue_WhenCreated_ThenSearchParameterShouldBeAddedToUnsupportedList()
        {
            var queryParameters = new[]
            {
                Tuple.Create(" ", "city"),
                Tuple.Create(string.Empty, string.Empty),
            };

            SearchOptions options = CreateSearchOptions(ResourceType.Patient.ToString(), queryParameters);
            Assert.NotNull(options);
            Assert.NotNull(options.UnsupportedSearchParams);
            Assert.Equal(2, options.UnsupportedSearchParams.Count);
            Assert.Equal(queryParameters.Take(1), options.UnsupportedSearchParams.Take(1));
            Assert.Equal(queryParameters.Skip(1).Take(1), options.UnsupportedSearchParams.Skip(1).Take(1));
        }

        [Fact]
        public void GivenSearchParametersWithEmptyKeyEmptyValueWithAnotherValidParameter_WhenCreated_ThenSearchParameterShouldBeAddedToUnsupportedList()
        {
            var queryParameters = new[]
            {
                Tuple.Create("patient", "city"),
                Tuple.Create(string.Empty, string.Empty),
            };

            SearchOptions options = CreateSearchOptions(ResourceType.Patient.ToString(), queryParameters);
            Assert.NotNull(options);
            Assert.NotNull(options.UnsupportedSearchParams);
            Assert.Single(options.UnsupportedSearchParams);
            Assert.Equal(queryParameters.Skip(1).Take(1), options.UnsupportedSearchParams);
        }

        [Fact]
        public void GivenSearchParametersWithEmptyKeyEmptyValueWithAnotherInvalidParameter_WhenCreated_ThenSearchParameterShouldBeAddedToUnsupportedList()
        {
            var queryParameters = new[]
            {
                Tuple.Create(string.Empty, "city"),
                Tuple.Create(string.Empty, string.Empty),
            };

            SearchOptions options = CreateSearchOptions(ResourceType.Patient.ToString(), queryParameters);
            Assert.NotNull(options);
            Assert.NotNull(options.UnsupportedSearchParams);
            Assert.Equal(2, options.UnsupportedSearchParams.Count);
            Assert.Equal(queryParameters.Take(1), options.UnsupportedSearchParams.Take(1));
            Assert.Equal(queryParameters.Skip(1).Take(1), options.UnsupportedSearchParams.Skip(1).Take(1));
        }

        [Fact]
        public void GivenASearchParamWithInvalidValue_WhenCreated_ThenSearchParamShouldBeAddedToUnsupportedList()
        {
            const string paramName1 = "_count";
            const string value1 = "";
            const string paramName2 = "address-city";
            const string value2 = "Seattle";

            var queryParameters = new[]
            {
                Tuple.Create(paramName1, value1),
                Tuple.Create(paramName2, value2),
            };

            SearchOptions options = CreateSearchOptions(
                resourceType: "Patient",
                queryParameters: queryParameters);

            Assert.NotNull(options);
            Assert.Equal(queryParameters.Take(1), options.UnsupportedSearchParams);
        }

        [Fact]
        public void GivenSearchWithUnsupportedSortValue_WhenCreated_ThenSortingShouldBeEmptyAndOperationOutcomeIssueCreated()
        {
            const string paramName = SearchParameterNames.ResourceType;

            const string errorMessage = "my error";

            _sortingValidator.ValidateSorting(default, out Arg.Any<IReadOnlyList<string>>()).ReturnsForAnyArgs(x =>
            {
                x[1] = new[] { errorMessage };
                return false;
            });

            var queryParameters = new[]
            {
                Tuple.Create(KnownQueryParameterNames.Sort, paramName),
                Tuple.Create(KnownQueryParameterNames.Sort, "-" + paramName),
            };

            SearchOptions options = CreateSearchOptions(
                resourceType: "Patient",
                queryParameters: queryParameters);

            Assert.NotNull(options);
            Assert.NotNull(options.Sort);
            Assert.Empty(options.Sort);

            Assert.Contains(_defaultFhirRequestContext.BundleIssues, issue => issue.Diagnostics == errorMessage);
        }

        [Theory]
        [InlineData(SearchParameterNames.LastUpdated, SortOrder.Ascending)]
        [InlineData("-" + SearchParameterNames.LastUpdated, SortOrder.Descending)]
        public void GivenSearchWithSupportedSortValue_WhenCreated_ThenSearchParamShouldBeAddedToSortList(string paramName, SortOrder sortOrder)
        {
            _sortingValidator.ValidateSorting(default, out var errors).ReturnsForAnyArgs(true);

            var queryParameters = new[]
            {
                Tuple.Create(KnownQueryParameterNames.Sort, paramName),
            };

            SearchOptions options = CreateSearchOptions(
                resourceType: "Patient",
                queryParameters: queryParameters);

            Assert.NotNull(options);
            Assert.NotNull(options.Sort);
            Assert.Equal((_lastUpdatedSearchParameterInfo, sortOrder), Assert.Single(options.Sort));
        }

        [Fact]
        public void GivenSearchWithAnInvalidSortValue_WhenCreated_ThenAnOperationOutcomeIssueIsCreated()
        {
            const string paramName = "unknownParameter";

            var queryParameters = new[]
            {
                Tuple.Create(KnownQueryParameterNames.Sort, paramName),
            };

            SearchOptions options = CreateSearchOptions(
                resourceType: "Patient",
                queryParameters: queryParameters);

            Assert.NotNull(options);
            Assert.NotNull(options.Sort);
            Assert.Empty(options.Sort);

            Assert.Contains(_defaultFhirRequestContext.BundleIssues, issue => issue.Code == OperationOutcomeConstants.IssueType.NotSupported);
        }

        [Theory]
        [Trait(Traits.Category, Categories.CompartmentSearch)]
        [InlineData(ResourceType.Patient, CompartmentType.Patient, "123")]
        [InlineData(ResourceType.Appointment, CompartmentType.Device, "abc")]
        [InlineData(ResourceType.Patient, CompartmentType.Encounter, "aaa")]
        [InlineData(ResourceType.Condition, CompartmentType.Practitioner, "9aa")]
        [InlineData(ResourceType.Patient, CompartmentType.RelatedPerson, "fdsfasfasfdas")]
        [InlineData(ResourceType.Claim, CompartmentType.Encounter, "ksd;/fkds;kfsd;kf")]
        public void GivenAValidCompartmentSearch_WhenCreated_ThenCorrectCompartmentSearchExpressionShouldBeGenerated(ResourceType resourceType, CompartmentType compartmentType, string compartmentId)
        {
            SearchOptions options = CreateSearchOptions(
                resourceType: resourceType.ToString(),
                queryParameters: null,
                compartmentType: compartmentType.ToString(),
                compartmentId: compartmentId);

            Assert.NotNull(options);
            ValidateMultiaryExpression(
                options.Expression,
                MultiaryOperator.And,
                e => ValidateResourceTypeSearchParameterExpression(e, resourceType.ToString()),
                e => ValidateCompartmentSearchExpression(e, compartmentType.ToString(), compartmentId));
        }

        [Theory]
        [Trait(Traits.Category, Categories.CompartmentSearch)]
        [InlineData(CompartmentType.Patient, "123")]
        [InlineData(CompartmentType.Device, "abc")]
        [InlineData(CompartmentType.Encounter, "aaa")]
        [InlineData(CompartmentType.Practitioner, "9aa")]
        [InlineData(CompartmentType.RelatedPerson, "fdsfasfasfdas")]
        [InlineData(CompartmentType.Encounter, "ksd;/fkds;kfsd;kf")]
        public void GivenAValidCompartmentSearchWithNullResourceType_WhenCreated_ThenCorrectCompartmentSearchExpressionShouldBeGenerated(CompartmentType compartmentType, string compartmentId)
        {
            SearchOptions options = CreateSearchOptions(
                resourceType: null,
                queryParameters: null,
                compartmentType: compartmentType.ToString(),
                compartmentId: compartmentId);

            Assert.NotNull(options);
            ValidateCompartmentSearchExpression(options.Expression, compartmentType.ToString(), compartmentId);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("12223a2424")]
        [InlineData("fsdfsdf")]
        [InlineData("patients")]
        [InlineData("encounter")]
        [InlineData("Devices")]
        public void GivenInvalidCompartmentType_WhenCreated_ThenExceptionShouldBeThrown(string invalidCompartmentType)
        {
            InvalidSearchOperationException exception = Assert.Throws<InvalidSearchOperationException>(() => CreateSearchOptions(
                resourceType: null,
                queryParameters: null,
                compartmentType: invalidCompartmentType,
                compartmentId: "123"));

            Assert.Equal(exception.Message, $"Compartment type {invalidCompartmentType} is invalid.");
        }

        [Theory]
        [InlineData("    ")]
        [InlineData("")]
        [InlineData("       ")]
        [InlineData("\t\t")]
        public void GivenInvalidCompartmentId_WhenCreated_ThenExceptionShouldBeThrown(string invalidCompartmentId)
        {
            InvalidSearchOperationException exception = Assert.Throws<InvalidSearchOperationException>(() => CreateSearchOptions(
                resourceType: ResourceType.Claim.ToString(),
                queryParameters: null,
                compartmentType: CompartmentType.Patient.ToString(),
                compartmentId: invalidCompartmentId));

            Assert.Equal("Compartment id is null or empty.", exception.Message);
        }

        [Theory]
        [InlineData(TotalType.Accurate)]
        [InlineData(TotalType.None)]
        public void GivenNoTotalParameter_WhenCreated_ThenDefaultSearchOptionsShouldHaveCountWhenConfiguredByDefault(TotalType type)
        {
            _coreFeatures.IncludeTotalInBundle = type;

            SearchOptions options = CreateSearchOptions(queryParameters: null);

            Assert.Equal(type, options.IncludeTotal);
        }

        [Fact]
        public void GivenTotalParameter_WhenCreated_ThenDefaultSearchOptionsShouldOverrideDefault()
        {
            _coreFeatures.IncludeTotalInBundle = TotalType.Accurate;

            SearchOptions options = CreateSearchOptions(queryParameters: new[] { Tuple.Create<string, string>("_total", "none"), });

            Assert.Equal(TotalType.None, options.IncludeTotal);
        }

        [Fact]
        public void GivenNoTotalParameterWithInvalidDefault_WhenCreated_ThenDefaultSearchOptionsThrowException()
        {
            _coreFeatures.IncludeTotalInBundle = TotalType.Estimate;

            Assert.Throws<SearchOperationNotSupportedException>(() => CreateSearchOptions(queryParameters: null));
        }

        [Fact]
        public void GivenNoCountParameter_WhenCreated_ThenDefaultSearchOptionShouldUseConfigurationValue()
        {
            _coreFeatures.MaxItemCountPerSearch = 10;
            _coreFeatures.DefaultItemCountPerSearch = 3;

            SearchOptions options = CreateSearchOptions();
            Assert.Equal(3, options.MaxItemCount);
        }

        [Fact]
        public void GivenCountParameterBelowThanMaximumAllowed_WhenCreated_ThenDefaultSearchOptionShouldBeCreatedAndCountParameterShouldBeUsed()
        {
            _coreFeatures.MaxItemCountPerSearch = 20;
            _coreFeatures.DefaultItemCountPerSearch = 1;

            SearchOptions options = CreateSearchOptions(queryParameters: new[] { Tuple.Create<string, string>("_count", "10"), });
            Assert.Equal(10, options.MaxItemCount);
        }

        [Fact]
        public void GivenCountParameterAboveThanMaximumAllowed_WhenCreated_ThenSearchOptionsAddIssueToContext()
        {
            _coreFeatures.MaxItemCountPerSearch = 10;
            _coreFeatures.DefaultItemCountPerSearch = 1;

            CreateSearchOptions(queryParameters: new[] { Tuple.Create<string, string>("_count", "11"), });

            Assert.Collection(_defaultFhirRequestContext.BundleIssues, issue => issue.Diagnostics.Contains("exceeds limit"));
        }

        [Fact]
        public void GivenSetCoreFeatureForIncludeCount_WhenCreated_ThenSearchOptionsHaveSameValue()
        {
            _coreFeatures.DefaultIncludeCountPerSearch = 9;

            SearchOptions options = CreateSearchOptions();
            Assert.Equal(_coreFeatures.DefaultIncludeCountPerSearch, options.IncludeCount);
        }

        [Fact]
        public void GivenSearchParameterText_WhenCreated_ThenSearchParameterShouldBeAddedToUnsupportedList()
        {
            var queryParameters = new[]
            {
                Tuple.Create(KnownQueryParameterNames.Text, "mobile"),
            };

            SearchOptions options = CreateSearchOptions(ResourceType.Patient.ToString(), queryParameters);
            Assert.NotNull(options);
            Assert.Single(options.UnsupportedSearchParams);
        }

        [Theory]
        [InlineData(ResourceVersionType.Latest)]
        [InlineData(ResourceVersionType.History)]
        [InlineData(ResourceVersionType.SoftDeleted)]
        [InlineData(ResourceVersionType.Latest | ResourceVersionType.History)]
        [InlineData(ResourceVersionType.Latest | ResourceVersionType.SoftDeleted)]
        [InlineData(ResourceVersionType.History | ResourceVersionType.SoftDeleted)]
        [InlineData(ResourceVersionType.Latest | ResourceVersionType.History | ResourceVersionType.SoftDeleted)]
        public void GivenIncludeHistoryAndDeletedParameters_WhenCreated_ThenSearchParametersShouldMatchInput(ResourceVersionType resourceVersionTypes)
        {
            SearchOptions options = CreateSearchOptions(ResourceType.Patient.ToString(), new List<Tuple<string, string>>(), resourceVersionTypes);
            Assert.NotNull(options);
            Assert.Equal(resourceVersionTypes, options.ResourceVersionTypes);
            Assert.Empty(options.UnsupportedSearchParams);
        }

        [Fact]
        public void GivenNotReferencedParameterWithWildcards_WhenCreated_ThenProperExpressionIsAdded()
        {
            _expressionParser.ParseNotReferenced(Arg.Any<string>()).Returns(new NotReferencedExpression(null, null, true));

            SearchOptions options = CreateSearchOptions(
                resourceType: ResourceType.Patient.ToString(),
                queryParameters: new[] { Tuple.Create(KnownQueryParameterNames.NotReferenced, "*:*") });
            Assert.NotNull(options);
            Assert.NotNull(options.Expression);
            Assert.Contains((options.Expression as MultiaryExpression).Expressions, expression => expression is NotReferencedExpression);
        }

        [Fact]
        public void GivenNotReferencedParameterWithInvalidValue_WhenCreated_ThenExceptionIsThrown()
        {
            var message = "test";
            _expressionParser.ParseNotReferenced(Arg.Any<string>()).Throws(new InvalidSearchOperationException(message));

            CreateSearchOptions(
                resourceType: ResourceType.Patient.ToString(),
                queryParameters: new[] { Tuple.Create(KnownQueryParameterNames.NotReferenced, "invalid") });

            Assert.Collection(_defaultFhirRequestContext.BundleIssues, issue => issue.Diagnostics.Contains(message));
        }

        [Fact]
        public void GivenMultipleIncludesContinuationTokens_WhenCreated_ThenExceptionShouldBeThrown()
        {
            const string encodedContinuationToken = "MTIz";

            Assert.Throws<InvalidSearchOperationException>(() => CreateSearchOptions(
                queryParameters: new[]
                {
                    Tuple.Create(KnownQueryParameterNames.IncludesContinuationToken, encodedContinuationToken),
                    Tuple.Create(KnownQueryParameterNames.IncludesContinuationToken, encodedContinuationToken),
                },
                isIncludesOperation: true));
        }

        [Theory]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        public void GivenIncludesContinuationToken_WhenCreated_ThenOperationOutcomeIssueShouldBeAddedForNonIncludesOperation(bool isIncludesOperation, int operationOutcomeIssueCount)
        {
            const string ct = "123";
            var options = CreateSearchOptions(
                queryParameters: new[]
                {
                    Tuple.Create(KnownQueryParameterNames.IncludesContinuationToken, ContinuationTokenEncoder.Encode(ct)),
                },
                isIncludesOperation: isIncludesOperation);

            var expectedCt = isIncludesOperation ? ct : null;
            Assert.Equal(expectedCt, options.IncludesContinuationToken);
            Assert.Equal(
                operationOutcomeIssueCount,
                _defaultFhirRequestContext.BundleIssues.Count(x => x.Diagnostics == Core.Resources.IncludesContinuationTokenIgnored));
        }

        [Theory]
        [InlineData(100, 100)]
        [InlineData(null, 1000)]
        [InlineData(int.MaxValue, 1000)]
        public void GivenAnIncludesCount_WhenCreated_ThenCorrectIncludeCountShouldBeSet(int? valueToSet, int valueExpected)
        {
            var parameters = valueToSet.HasValue
                ? new List<Tuple<string, string>> { Tuple.Create(KnownQueryParameterNames.IncludesCount, valueToSet.Value.ToString()) }
                : null;
            SearchOptions options = CreateSearchOptions(queryParameters: parameters);

            Assert.NotNull(options);
            Assert.Equal(valueExpected, options.IncludeCount);
        }

        [Fact]
        public void GivenAnIncludesOperationRequest_WhenIncludesContinuationTokenIsMissing_ThenExceptionShouldBeThrown()
        {
            Assert.Throws<BadRequestException>(() => CreateSearchOptions(isIncludesOperation: true));
        }

        [Theory]
        [MemberData(nameof(GetSearchParameterTestData))]
        public void Create_AddsFineGrainedAccessControlWithSearchParametersExpressions_UsingMemberData(string resourceType, List<ScopeRestriction> scopeRestrictions, List<Tuple<string, string>> queryParameters, string expectedSubstring)
        {
            // Arrange
            var stubExpressionParser = Substitute.For<IExpressionParser>();
            stubExpressionParser.Parse(Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(x => new StubExpression($"{x.ArgAt<string>(1)}={x.ArgAt<string>(2)}"));
            stubExpressionParser.ParseInclude(Arg.Any<string[]>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<IReadOnlyCollection<string>>())
                .Returns((IncludeExpression)null);

            var stubResourceTypeSearchParameter = new StubSearchParameterInfo("ResourceType", "ResourceType");
            var stubSearchParameterDefinitionManager = Substitute.For<ISearchParameterDefinitionManager>();
            stubSearchParameterDefinitionManager.GetSearchParameter(Arg.Any<string>(), Arg.Any<string>())
                .Returns(stubResourceTypeSearchParameter);
            ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver resolver = () => stubSearchParameterDefinitionManager;

            var fhirRequestContext = new DefaultFhirRequestContext
            {
                AccessControlContext = new AccessControlContext
                {
                    ApplyFineGrainedAccessControl = true,
                    ApplyFineGrainedAccessControlWithSearchParameters = true,
                },
            };

            foreach (var restriction in scopeRestrictions)
            {
                fhirRequestContext.AccessControlContext.AllowedResourceActions.Add(restriction);
            }

            var contextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
            contextAccessor.RequestContext.Returns(fhirRequestContext);

            var dummySortingValidator = Substitute.For<ISortingValidator>();
            dummySortingValidator.ValidateSorting(
                Arg.Any<IReadOnlyList<(SearchParameterInfo, Core.Features.Search.SortOrder)>>(),
                out _).Returns(true);

            var factory = new SearchOptionsFactory(
                stubExpressionParser,
                resolver,
                new OptionsWrapper<CoreFeatureConfiguration>(_coreFeatures),
                contextAccessor,
                Substitute.For<ISortingValidator>(),
                new ExpressionAccessControl(contextAccessor),
                Substitute.For<IIgnixaSearchOptionsAdapter>(),
                new IgnixaSearchTenantAccessor(contextAccessor),
                NullLogger<SearchOptionsFactory>.Instance);

            // Act
            SearchOptions options = factory.Create(resourceType, queryParameters, onlyIds: false, isIncludesOperation: false);

            // Assert
            if (string.IsNullOrEmpty(expectedSubstring))
            {
                Assert.Null(options.Expression);
            }
            else
            {
                Assert.NotNull(options.Expression);
                string expressionText = options.Expression.ToString();
                Assert.Contains(expectedSubstring, expressionText, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        // ---------------------------------------------------------------------------
        // SMART clinical scope translation into the Ignixa allow-list
        //
        // These guard a security boundary. IgnixaAccessControlTranslated is what tells the SQL router it may
        // hand a scope-restricted request to the Ignixa compiler; if it is set for a case the translation does
        // not actually cover, the compiled plan enforces less than the legacy generator would.
        // ---------------------------------------------------------------------------

        [Fact]
        public void Create_GivenNoUnsupportedParameters_MarksTheDropSetsAsAgreeing()
        {
            SearchOptions options = CreateSearchOptions(
                resourceType: "Patient",
                queryParameters: new[] { Tuple.Create("_id", "abc") });

            Assert.Empty(options.UnsupportedSearchParams);
            Assert.True(options.IgnixaUnsupportedParamsAgreeWithLegacy);
        }

        [Fact]
        public void Create_GivenAParameterNeitherEngineUnderstands_MarksTheDropSetsAsAgreeing()
        {
            // Both parsers reject the same unknown parameter, so the two engines search the same rows and report
            // the same issue. That is safe to route to Ignixa, which is the whole point of tracking agreement
            // rather than simply refusing any request with an unsupported parameter.
            const string bogus = "totallyBogusParameter";
            _expressionParser
                .Parse(Arg.Any<string[]>(), bogus, Arg.Any<string>())
                .Throws(new SearchParameterNotSupportedException(typeof(Patient), bogus));
            _ignixaAdapter.Build(Arg.Any<string>(), Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<int?>())
                .Returns(new global::Ignixa.Search.Models.SearchOptions { UnsupportedParams = new List<string> { bogus } });

            SearchOptions options = CreateSearchOptions(
                resourceType: "Patient",
                queryParameters: new[] { Tuple.Create(bogus, "x") });

            Assert.Contains(options.UnsupportedSearchParams, p => p.Item1 == bogus);
            Assert.True(options.IgnixaUnsupportedParamsAgreeWithLegacy);
        }

        [Fact]
        public void Create_GivenAParameterOnlyIgnixaRejects_DoesNotMarkTheDropSetsAsAgreeing()
        {
            // Legacy parsed the parameter and will filter on it; Ignixa dropped it. Routing this to Ignixa would
            // return a superset of the correct rows, which is the fail-open direction this flag exists to catch.
            const string param = "code";
            _ignixaAdapter.Build(Arg.Any<string>(), Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<int?>())
                .Returns(new global::Ignixa.Search.Models.SearchOptions { UnsupportedParams = new List<string> { param } });

            SearchOptions options = CreateSearchOptions(
                resourceType: "Patient",
                queryParameters: new[] { Tuple.Create(param, "x") });

            Assert.Contains(options.UnsupportedSearchParams, p => p.Item1 == param);
            Assert.False(options.IgnixaUnsupportedParamsAgreeWithLegacy);
        }

        [Fact]
        public void Create_GivenAParameterOnlyLegacyRejects_DoesNotMarkTheDropSetsAsAgreeing()
        {
            // "_text" is refused outright by the legacy factory for every resource type. If the Ignixa parser
            // accepts it, Ignixa would apply a filter legacy ignores and return a subset of legacy's rows, so the
            // request must stay on the legacy path.
            SearchOptions options = CreateSearchOptions(
                resourceType: "Patient",
                queryParameters: new[] { Tuple.Create("_text", "some text") });

            Assert.Contains(options.UnsupportedSearchParams, p => p.Item1 == "_text");
            Assert.False(options.IgnixaUnsupportedParamsAgreeWithLegacy);
        }

        [Fact]
        public void Create_GivenClinicalScopes_TranslatesThemIntoTheIgnixaAllowList()
        {
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(new ScopeRestriction("Patient", DataActions.Read, "patient"));
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(new ScopeRestriction("Observation", DataActions.Read, "patient"));

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // The full scope list, not the scope-and-requested intersection legacy ANDs into the match filter.
            // Observation is kept even though this is a Patient search because the include stages need it: it is
            // the set legacy carries on IncludeExpression.AllowedResourceTypesByScope. The match set is already
            // restricted to Patient, so intersecting it with this list still yields exactly Patient.
            Assert.Equal(new[] { "Patient", "Observation" }, options.IgnixaOptions.AllowedResourceTypes);
            Assert.True(options.IgnixaAccessControlTranslated);
        }

        [Fact]
        public void Create_GivenAWildcardScope_LeavesTheAllowListEmptyButMarksItTranslated()
        {
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(new ScopeRestriction(KnownResourceTypes.All, DataActions.Read, "user"));

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // "All" grants every type, which is what an absent allow-list already means to the compiler. Expanding
            // it to the full type list would change the emitted plan for no benefit, so it stays empty -- but the
            // translation is genuinely complete, so the router may proceed.
            Assert.Empty(options.IgnixaOptions.AllowedResourceTypes);
            Assert.True(options.IgnixaAccessControlTranslated);
        }

        [Fact]
        public void Create_GivenScopesCarryingSearchParameters_TranslatesThemIntoAccessConstraints()
        {
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControlWithSearchParameters = true;
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(
                new ScopeRestriction("Patient", DataActions.Read, "patient", new SearchParams().Add("code", "foo")));

            global::Ignixa.Search.Expressions.Expression scopePredicate = StubScopePredicate();

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // A SMART v2 scope restricts which *instances* of a permitted type are visible, which is exactly what
            // AccessConstraint expresses. Both halves must be present: the allow-list denies the types the scopes
            // never granted, the constraint narrows the one they did.
            Assert.Equal(new[] { "Patient" }, options.IgnixaOptions.AllowedResourceTypes);
            global::Ignixa.Search.Models.AccessConstraint constraint = Assert.Single(options.IgnixaOptions.AccessConstraints);
            Assert.Equal("Patient", constraint.ResourceType);
            Assert.Same(scopePredicate, constraint.Predicate);
            Assert.True(options.IgnixaAccessControlTranslated);
        }

        [Fact]
        public void Create_GivenTwoScopesOnOneTypeCarryingSearchParameters_CollapsesThemByConjunction()
        {
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControlWithSearchParameters = true;
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(
                new ScopeRestriction("Patient", DataActions.Read, "patient", new SearchParams().Add("code", "foo")));
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(
                new ScopeRestriction("Patient", DataActions.Read, "user", new SearchParams().Add("status", "active")));

            StubScopePredicate();

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // AccessConstraints permits at most one entry per resource type, and two scopes on one type are two
            // independent restrictions that must both hold. Keeping only one would widen the grant; ORing them
            // would too. Conjunction is the same reading legacy takes when it ANDs a type's scope legs together.
            global::Ignixa.Search.Models.AccessConstraint constraint = Assert.Single(options.IgnixaOptions.AccessConstraints);
            var conjunction = Assert.IsType<global::Ignixa.Search.Expressions.MultiaryExpression>(constraint.Predicate);
            Assert.Equal(global::Ignixa.Search.Expressions.MultiaryOperator.And, conjunction.MultiaryOperation);
            Assert.Equal(2, conjunction.Expressions.Count);
            Assert.True(options.IgnixaAccessControlTranslated);
        }

        [Fact]
        public void Create_GivenScopeSearchParametersIgnixaCannotParse_DoesNotMarkThemTranslated()
        {
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControlWithSearchParameters = true;
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(
                new ScopeRestriction("Patient", DataActions.Read, "patient", new SearchParams().Add("code", "foo")));

            _ignixaAdapter.Build("Patient", Arg.Is<IReadOnlyList<Tuple<string, string>>>(p => p.Any(t => t.Item1 == "code")), Arg.Any<int?>())
                .Returns(new global::Ignixa.Search.Models.SearchOptions { UnsupportedParams = new List<string> { "code" } });

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // Ignixa dropped part of the restriction. Forwarding what it did understand would grant more than the
            // scope allows, so the request goes back to legacy rather than being enforced approximately.
            Assert.False(options.IgnixaAccessControlTranslated);
        }

        [Fact]
        public void Create_GivenScopesCarryingSearchParametersButTheEnforcementFlagIsOff_TranslatesTheTypeListOnly()
        {
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControlWithSearchParameters = false;
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(
                new ScopeRestriction("Patient", DataActions.Read, "patient", new SearchParams().Add("code", "foo")));

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // With the flag off, CheckFineGrainedAccessControl builds the parameter union and then discards it,
            // keeping only the type restriction. Mirroring that literally is the point: emitting a constraint here
            // would make Ignixa stricter than legacy and the differential tests would disagree.
            Assert.Equal(new[] { "Patient" }, options.IgnixaOptions.AllowedResourceTypes);
            Assert.Empty(options.IgnixaOptions.AccessConstraints);
            Assert.True(options.IgnixaAccessControlTranslated);
        }

        [Fact]
        public void Create_GivenAWildcardScopeCarryingSearchParameters_DoesNotMarkItTranslated()
        {
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControlWithSearchParameters = true;
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(
                new ScopeRestriction(KnownResourceTypes.All, DataActions.Read, "user", new SearchParams().Add("code", "foo")));

            StubScopePredicate();

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // Legacy folds a wildcard scope's parameters into the search itself, so they constrain every requested
            // type at once. AccessConstraint is keyed by resource type and cannot say "all types", so there is no
            // faithful spelling and the request stays on the legacy path.
            Assert.False(options.IgnixaAccessControlTranslated);
        }

        [Fact]
        public void Create_GivenNoGrantedResources_NamesAnUnmatchableTypeAndMarksItTranslated()
        {
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // Legacy blocks the whole query when no scope grants anything. An empty allow-list means "inert" to the
            // compiler, so the denial is spelled as a type name that cannot resolve: the compiler keeps an
            // unresolvable name as its unmatchable sentinel rather than dropping it, which blocks every row.
            Assert.Equal(new[] { "none" }, options.IgnixaOptions.AllowedResourceTypes);
            Assert.True(options.IgnixaAccessControlTranslated);
        }

        [Fact]
        public void Create_GivenCompartmentAccess_TranslatesTheScopeAllowListAndTheCompartmentUnion()
        {
            StubScopePredicate();
            StubDevicePatientSearchParameter();
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.CompartmentResourceType = "Patient";
            _defaultFhirRequestContext.AccessControlContext.CompartmentId = "123";
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(new ScopeRestriction("Patient", DataActions.Read, "patient"));

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // The two halves are tracked separately because they gate separately: the scope list becomes an
            // allow-list, the compartment becomes a union of membership legs ANDed into the expression.
            Assert.True(options.IgnixaAccessControlTranslated);
            Assert.True(options.IgnixaSmartCompartmentTranslated);
            Assert.Equal(new[] { "Patient" }, options.IgnixaOptions.AllowedResourceTypes);
            Assert.Contains(
                DescendantsOf(options.IgnixaOptions.Expression),
                e => e is global::Ignixa.Search.Expressions.UnionExpression);
        }

        [Fact]
        public void Create_GivenCompartmentAccessWithNoDevicePatientParameter_DoesNotMarkTheCompartmentTranslated()
        {
            StubScopePredicate();
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.CompartmentResourceType = "Patient";
            _defaultFhirRequestContext.AccessControlContext.CompartmentId = "123";
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(new ScopeRestriction("Patient", DataActions.Read, "patient"));

            // The device restriction is on but Device.patient is undefined (the R5 shape). Legacy silently drops
            // the restriction and treats Device as universal; translating that would turn a narrowing into a
            // widening, so the compartment stays untranslated and the request keeps the legacy path.
            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            Assert.False(options.IgnixaSmartCompartmentTranslated);
        }

        [Fact]
        public void Create_GivenCompartmentAccessWithNoIgnixaOptions_DoesNotMarkTheCompartmentTranslated()
        {
            StubScopePredicate();
            StubDevicePatientSearchParameter();
            _defaultFhirRequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _defaultFhirRequestContext.AccessControlContext.CompartmentResourceType = "Patient";
            _defaultFhirRequestContext.AccessControlContext.CompartmentId = "123";
            _defaultFhirRequestContext.AccessControlContext.AllowedResourceActions.Add(new ScopeRestriction("Patient", DataActions.Read, "patient"));

            // No Ignixa options means there is no expression to AND the union into. The flag must stay false so
            // the router keeps the request on legacy rather than running an unrestricted compartment search.
            _ignixaAdapter
                .Build(Arg.Any<string>(), Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<int?>())
                .Returns((global::Ignixa.Search.Models.SearchOptions)null);

            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            Assert.False(options.IgnixaSmartCompartmentTranslated);
        }

        private static IEnumerable<global::Ignixa.Search.Expressions.Expression> DescendantsOf(global::Ignixa.Search.Expressions.Expression expression)
        {
            if (expression == null)
            {
                yield break;
            }

            yield return expression;

            IEnumerable<global::Ignixa.Search.Expressions.Expression> children = expression switch
            {
                global::Ignixa.Search.Expressions.MultiaryExpression multiary => multiary.Expressions,
                global::Ignixa.Search.Expressions.UnionExpression union => union.Expressions,
                _ => Array.Empty<global::Ignixa.Search.Expressions.Expression>(),
            };

            foreach (global::Ignixa.Search.Expressions.Expression child in children)
            {
                foreach (global::Ignixa.Search.Expressions.Expression descendant in DescendantsOf(child))
                {
                    yield return descendant;
                }
            }
        }

        [Fact]
        public void Create_WithoutFineGrainedAccessControl_LeavesTheAllowListEmptyAndUntranslated()
        {
            SearchOptions options = CreateSearchOptions(resourceType: "Patient");

            // Nothing to translate. The flag stays false, but the router never consults it for a request that
            // carries no access control predicate, so ordinary searches are unaffected.
            Assert.Empty(options.IgnixaOptions.AllowedResourceTypes);
            Assert.False(options.IgnixaAccessControlTranslated);
        }

        /// <summary>
        /// Makes the Ignixa adapter return a real expression for any <em>non-empty</em> parameter list, which is
        /// how a scope's own parameters are distinguished from the request's here: the tests that call this issue
        /// a search with no query parameters, so only the scope predicate build matches.
        /// </summary>
        private global::Ignixa.Search.Expressions.Expression StubScopePredicate()
        {
            global::Ignixa.Search.Expressions.Expression predicate =
                global::Ignixa.Search.Expressions.Expression.Missing(global::Ignixa.Search.Expressions.FieldName.TokenCode, componentIndex: null);

            _ignixaAdapter
                .Build(Arg.Any<string>(), Arg.Is<IReadOnlyList<Tuple<string, string>>>(p => p != null && p.Count > 0), Arg.Any<int?>())
                .Returns(new global::Ignixa.Search.Models.SearchOptions { Expression = predicate });

            return predicate;
        }

        /// <summary>
        /// Makes <c>Device.patient</c> resolvable, which is what the SMART compartment device restriction keys on.
        /// Without it the factory takes the fail-closed path and leaves the compartment untranslated.
        /// </summary>
        private void StubDevicePatientSearchParameter()
        {
            var devicePatient = new SearchParameter
            {
                Name = "patient",
                Code = "patient",
                Type = SearchParamType.Reference,
                Url = "http://hl7.org/fhir/SearchParameter/Device-patient",
            }.ToInfo();

            _searchParameterDefinitionManager
                .TryGetSearchParameter(KnownResourceTypes.Device, "patient", out Arg.Any<SearchParameterInfo>())
                .Returns(x =>
                {
                    x[2] = devicePatient;
                    return true;
                });
        }

        [Fact]
        public void Create_WithoutAGlobalEndSurrogateId_LeavesQueryHintsNull()
        {
            // The router's query-hints gate documents itself as unreachable, and this is the invariant that makes
            // it so: hints exist only for the export/bulk-update time-travel shape, which SearchImpl intercepts
            // before routing. A change here would silently make hint-carrying requests routable.
            SearchOptions withoutHints = CreateSearchOptions(
                queryParameters: new[] { Tuple.Create(KnownQueryParameterNames.StartSurrogateId, "1"), Tuple.Create(KnownQueryParameterNames.EndSurrogateId, "9") });

            Assert.Null(withoutHints.QueryHints);

            SearchOptions withHints = CreateSearchOptions(
                queryParameters: new[]
                {
                    Tuple.Create(KnownQueryParameterNames.StartSurrogateId, "1"),
                    Tuple.Create(KnownQueryParameterNames.EndSurrogateId, "9"),
                    Tuple.Create(KnownQueryParameterNames.GlobalEndSurrogateId, "9"),
                });

            Assert.NotNull(withHints.QueryHints);
            Assert.Contains(withHints.QueryHints, hint => hint.Param == KnownQueryParameterNames.GlobalEndSurrogateId);
        }

        private SearchOptions CreateSearchOptions(
            string resourceType = DefaultResourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters = null,
            ResourceVersionType resourceVersionTypes = ResourceVersionType.Latest,
            string compartmentType = null,
            string compartmentId = null,
            bool isIncludesOperation = false)
        {
            return _factory.Create(compartmentType, compartmentId, resourceType, queryParameters, resourceVersionTypes: resourceVersionTypes, isIncludesOperation: isIncludesOperation);
        }

        // A simple stub implementation for Expression used in our test.
        private class StubExpression : Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression
        {
            private readonly string _description;

            public StubExpression(string description)
            {
                _description = description;
            }

            public override string ToString() => _description;

            public override void AddValueInsensitiveHashCode(ref HashCode hashCode)
            {
                hashCode.Add(_description);
            }

            public override bool ValueInsensitiveEquals(Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression other) =>
                other is StubExpression se && se._description == _description;

            public override TOutput AcceptVisitor<TContext, TOutput>(IExpressionVisitor<TContext, TOutput> visitor, TContext context)
            {
                throw new NotImplementedException();
            }
        }

        // A stub for SearchParameterInfo.
        private class StubSearchParameterInfo : SearchParameterInfo
        {
            public StubSearchParameterInfo(string name, string code)
                : base(name, code)
            {
            }

            public override string ToString() => Code;
        }

        // A dummy implementation of ExpressionAccessControl that does nothing.
        private class DummyExpressionAccessControl : ExpressionAccessControl
        {
            public DummyExpressionAccessControl()
                : base(null)
            {
            }
        }
    }
}
