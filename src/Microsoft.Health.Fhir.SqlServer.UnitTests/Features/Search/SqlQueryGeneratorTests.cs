// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Search;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions.Visitors;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions.Visitors.QueryGenerators;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search.Expressions.Visitors.QueryGenerators;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.Health.SqlServer;
using Microsoft.Health.SqlServer.Features.Schema;
using Microsoft.Health.SqlServer.Features.Storage;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;
using Xunit.Sdk;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Search;

[Trait(Traits.OwningTeam, OwningTeam.Fhir)]
[Trait(Traits.Category, Categories.Search)]
public class SqlQueryGeneratorTests : IClassFixture<ModelInfoProviderFixture>, IDisposable
{
    private readonly ISqlServerFhirModel _fhirModel;
    private readonly IVectorSearchCapabilityProvider _vectorSearchCapabilityProvider;
    private readonly VectorSearchConfiguration _vectorSearchConfiguration = new();
    private readonly SearchParamTableExpressionQueryGeneratorFactory _queryGeneratorFactory;
    private readonly SchemaInformation _schemaInformation = new(SchemaVersionConstants.Min, SchemaVersionConstants.Max);
    private readonly IndentedStringBuilder _strBuilder = new(new StringBuilder());
    private readonly Data.SqlClient.SqlCommand _sqlCommand = new();
    private readonly SqlQueryGenerator _queryGenerator;

    public SqlQueryGeneratorTests(ModelInfoProviderFixture modelInfoProviderFixture)
    {
        _ = modelInfoProviderFixture;
        _fhirModel = Substitute.For<ISqlServerFhirModel>();
        _vectorSearchCapabilityProvider = Substitute.For<IVectorSearchCapabilityProvider>();
        _vectorSearchCapabilityProvider.EmbeddingModelId.Returns((short)17);

        // Create real instances instead of mocking since factory is internal
        var searchParameterToSearchValueTypeMap = new SearchParameterToSearchValueTypeMap();
        _queryGeneratorFactory = new SearchParamTableExpressionQueryGeneratorFactory(
            searchParameterToSearchValueTypeMap,
            _vectorSearchCapabilityProvider,
            Options.Create(_vectorSearchConfiguration));

        _schemaInformation.Current = SchemaVersionConstants.Max;

        HashingSqlQueryParameterManager parameters = new(new SqlQueryParameterManager(_sqlCommand.Parameters));

        _queryGenerator = new(
            _strBuilder,
            parameters,
            _fhirModel,
            _schemaInformation,
            _queryGeneratorFactory,
            false,
            false);
    }

    public void Dispose()
    {
        _sqlCommand.Dispose();
    }

    [Fact]
    public void GivenASearchTypeLatestResources_WhenSqlGenerated_ThenSqlFiltersForLatestOnly()
    {
        Expression predicate = Expression.And([new SearchParameterExpression(new SearchParameterInfo("_type", "_type"), new StringExpression(StringOperator.Equals, FieldName.String, null, "Patient", false))]);
        SqlRootExpression sqlExpression = new([new(null, predicate, SearchParamTableExpressionKind.All)], new List<SearchParameterExpressionBase>());
        SearchOptions searchOptions = new()
        {
            Sort = [],
            ResourceVersionTypes = ResourceVersionType.Latest,
        };

        var output = _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        Assert.Contains("IsHistory = 0", _strBuilder.ToString());
        Assert.Contains("IsDeleted = 0", _strBuilder.ToString());
    }

    [Fact]
    public void GivenASearchTypeForSoftDeletedOnly_WhenSqlGenerated_ThenFilterForSoftDeletedInSql()
    {
        Expression predicate = Expression.And([new SearchParameterExpression(new SearchParameterInfo("_type", "_type"), new StringExpression(StringOperator.Equals, FieldName.String, null, "Patient", false))]);
        SqlRootExpression sqlExpression = new([new(null, predicate, SearchParamTableExpressionKind.All)], new List<SearchParameterExpressionBase>());
        SearchOptions searchOptions = new()
        {
            Sort = [],
            ResourceVersionTypes = ResourceVersionType.SoftDeleted,
        };

        var output = _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        Assert.Contains("IsDeleted = 1", _strBuilder.ToString());
    }

    [Fact]
    public void GivenASearchTypeForHistoryOnly_WhenSqlGenerated_ThenFilterForHistoryInSql()
    {
        Expression predicate = Expression.And([new SearchParameterExpression(new SearchParameterInfo("_type", "_type"), new StringExpression(StringOperator.Equals, FieldName.String, null, "Patient", false))]);
        SqlRootExpression sqlExpression = new([new(null, predicate, SearchParamTableExpressionKind.All)], new List<SearchParameterExpressionBase>());
        SearchOptions searchOptions = new()
        {
            Sort = [],
            ResourceVersionTypes = ResourceVersionType.History,
        };

        var output = _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        Assert.Contains("History = 1", _strBuilder.ToString());
    }

    [Fact]
    public void GivenASearchTypeForLatestHistorySoftDeleted_WhenSqlGenerated_ThenFiltersArentInSql()
    {
        Expression predicate = Expression.And([new SearchParameterExpression(new SearchParameterInfo("_type", "_type"), new StringExpression(StringOperator.Equals, FieldName.String, null, "Patient", false))]);
        SqlRootExpression sqlExpression = new([new(null, predicate, SearchParamTableExpressionKind.All)], new List<SearchParameterExpressionBase>());
        SearchOptions searchOptions = new()
        {
            Sort = [],
            ResourceVersionTypes = ResourceVersionType.Latest | ResourceVersionType.History | ResourceVersionType.SoftDeleted,
        };

        var output = _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        Assert.DoesNotContain("IsHistory =", _strBuilder.ToString());
        Assert.DoesNotContain("IsDeleted =", _strBuilder.ToString());
    }

    [Fact]
    public void GivenASearchTypeForHistorySoftDeleted_WhenSqlGenerated_ThenSqlFiltersOutLatest()
    {
        Expression predicate = Expression.And([new SearchParameterExpression(new SearchParameterInfo("_type", "_type"), new StringExpression(StringOperator.Equals, FieldName.String, null, "Patient", false))]);
        SqlRootExpression sqlExpression = new([new(null, predicate, SearchParamTableExpressionKind.All)], new List<SearchParameterExpressionBase>());
        SearchOptions searchOptions = new()
        {
            Sort = [],
            ResourceVersionTypes = ResourceVersionType.History | ResourceVersionType.SoftDeleted,
        };

        var output = _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        Assert.Contains("IsHistory = 1", _strBuilder.ToString());
        Assert.Contains("IsDeleted = 1", _strBuilder.ToString());
    }

    [Fact]
    public void GivenReferenceSearchParameterWithMultipleTargetTypes_WhenSqlGenerated_ThenSqlIncludesOrClauseForReferenceResourceTypeId()
    {
        // Setup mock to return resource type IDs
        _fhirModel.TryGetResourceTypeId("Patient", out Arg.Any<short>())
            .Returns(x =>
            {
                x[1] = (short)1;
                return true;
            });
        _fhirModel.TryGetResourceTypeId("Practitioner", out Arg.Any<short>())
            .Returns(x =>
            {
                x[1] = (short)2;
                return true;
            });
        _fhirModel.GetSearchParamId(Arg.Any<Uri>()).Returns((short)100);

        // Create a reference search parameter with multiple target types (like Observation.patient)
        var referenceParam = new SearchParameterInfo(
            "patient",
            "patient",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/Observation-patient"),
            null,
            "Observation.subject",
            new[] { "Patient", "Practitioner" });

        // Create expression with OR of multiple target types + IS NULL (simulating UntypedReferenceRewriter output)
        Expression predicate = Expression.SearchParameter(
            referenceParam,
            Expression.And(
                Expression.StringEquals(FieldName.ReferenceResourceId, null, "test-id", false),
                Expression.Or(
                    Expression.StringEquals(FieldName.ReferenceResourceType, null, "Patient", false),
                    Expression.StringEquals(FieldName.ReferenceResourceType, null, "Practitioner", false),
                    Expression.Missing(FieldName.ReferenceResourceType, null))));

        var queryGenerator = predicate.AcceptVisitor(_queryGeneratorFactory, null);
        SqlRootExpression sqlExpression = new([new(queryGenerator, predicate, SearchParamTableExpressionKind.Normal)], new List<SearchParameterExpressionBase>());
        SearchOptions searchOptions = new()
        {
            Sort = [],
            ResourceVersionTypes = ResourceVersionType.Latest,
        };

        _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        string generatedSql = _strBuilder.ToString();

        // Verify the SQL contains ReferenceResourceTypeId with OR clause and IS NULL
        // This confirms that the VisitMultiary method in SearchParameterQueryGenerator
        // correctly handles the OR expression generated by UntypedReferenceRewriter
        Assert.Contains("ReferenceResourceTypeId", generatedSql);
        Assert.Contains(" OR ", generatedSql);
        Assert.Contains("IS NULL", generatedSql);

        // Verify both target resource type IDs appear as separate equality checks
        // Patient=1 and Practitioner=2, each generating "ReferenceResourceTypeId = @pN"
        int typeIdOccurrences = generatedSql.Split("ReferenceResourceTypeId").Length - 1;
        Assert.True(typeIdOccurrences >= 3, $"Expected ReferenceResourceTypeId to appear at least 3 times (2 type equality checks + 1 IS NULL), but found {typeIdOccurrences} in: {generatedSql}");

        // Verify both type IDs were passed as parameters by checking the mock was called
        _fhirModel.Received(1).TryGetResourceTypeId("Patient", out Arg.Any<short>());
        _fhirModel.Received(1).TryGetResourceTypeId("Practitioner", out Arg.Any<short>());
    }

    [Theory]
    [InlineData(false, "refTarget")]
    [InlineData(true, "refSource")]
    public void GivenSmartCompartmentInclude_WhenSqlGenerated_ThenCandidateMembershipIsCheckedBeforeBranchLimit(
        bool reversed,
        string candidateAlias)
    {
        // Arrange
        var includeParameterUrl = new Uri("http://hl7.org/fhir/SearchParameter/Observation-subject");
        var membershipParameterUrl = new Uri("http://hl7.org/fhir/SearchParameter/DiagnosticReport-subject");
        var includeParameter = new SearchParameterInfo(
            "subject",
            "subject",
            SearchParamType.Reference,
            includeParameterUrl,
            null,
            "Observation.subject",
            ["Patient"]);
        var includeExpression = new IncludeExpression(
            ["Observation"],
            includeParameter,
            "Observation",
            "Patient",
            null,
            false,
            reversed,
            false);
        var membership = new SmartCompartmentMembershipContext(
            "Patient",
            "patient-a",
            ["Practitioner"],
            [new SmartCompartmentMembershipRule("DiagnosticReport", [membershipParameterUrl])]);
        SqlRootExpression sqlExpression = new(
            [
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.All),
                new SearchParamTableExpression(IncludeQueryGenerator.Instance, includeExpression, SearchParamTableExpressionKind.Include),
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.IncludeLimit),
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.IncludeUnionAll),
            ],
            [],
            membership);
        SearchOptions searchOptions = new()
        {
            IncludeCount = 1,
            MaxItemCount = 10,
            Sort = [],
            ResourceVersionTypes = ResourceVersionType.Latest,
        };

        ConfigureResourceTypeIds();
        _fhirModel.GetSearchParamId(includeParameterUrl).Returns((short)40);
        _fhirModel.GetSearchParamId(membershipParameterUrl).Returns((short)41);

        // Act
        _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        // Assert
        string generatedSql = _strBuilder.ToString();
        Assert.Contains("smartCompartmentMembership", generatedSql);
        Assert.Contains($"smartCompartmentMembership.ResourceTypeId = {candidateAlias}.ResourceTypeId", generatedSql);
        Assert.Contains($"smartCompartmentMembership.ResourceSurrogateId = {candidateAlias}.ResourceSurrogateId", generatedSql);
        Assert.Contains("smartCompartmentMembership.BaseUri IS NULL", generatedSql);
        Assert.DoesNotContain("OPTION (RECOMPILE)", generatedSql);
        _fhirModel.Received(1).GetSearchParamId(membershipParameterUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GivenIncludeWithoutSmartCompartmentMembership_WhenSqlGenerated_ThenNoCandidatePredicateIsEmitted(bool reversed)
    {
        // Arrange - same include shape as the SMART compartment test, but no membership attached
        // (non-SMART request). The candidate authorization predicate must not appear.
        var includeParameterUrl = new Uri("http://hl7.org/fhir/SearchParameter/Observation-subject");
        var includeParameter = new SearchParameterInfo(
            "subject",
            "subject",
            SearchParamType.Reference,
            includeParameterUrl,
            null,
            "Observation.subject",
            ["Patient"]);
        var includeExpression = new IncludeExpression(
            ["Observation"],
            includeParameter,
            "Observation",
            "Patient",
            null,
            false,
            reversed,
            false);
        SqlRootExpression sqlExpression = new(
            [
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.All),
                new SearchParamTableExpression(IncludeQueryGenerator.Instance, includeExpression, SearchParamTableExpressionKind.Include),
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.IncludeLimit),
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.IncludeUnionAll),
            ],
            []);
        SearchOptions searchOptions = new()
        {
            IncludeCount = 1,
            MaxItemCount = 10,
            Sort = [],
            ResourceVersionTypes = ResourceVersionType.Latest,
        };

        ConfigureResourceTypeIds();
        _fhirModel.GetSearchParamId(includeParameterUrl).Returns((short)40);

        // Act
        _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        // Assert
        string generatedSql = _strBuilder.ToString();
        Assert.DoesNotContain("smartCompartmentMembership", generatedSql);
        Assert.DoesNotContain("smartCompartmentRoot", generatedSql);
    }

    [Fact]
    public void GivenObservationCompartmentDefinition_WhenMembershipCreated_ThenFocusIsNotAMembershipParameter()
    {
        // Arrange
        var subject = new SearchParameterInfo(
            "subject",
            "subject",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/Observation-subject"),
            null,
            "Observation.subject",
            ["Patient"]);
        var focus = new SearchParameterInfo(
            "focus",
            "focus",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/Observation-focus"),
            null,
            "Observation.focus",
            ["Patient"]);
        SqlCompartmentSearchRewriter rewriter = CreateCompartmentRewriter(
            "Observation",
            ["subject"],
            new Dictionary<string, SearchParameterInfo>
            {
                ["subject"] = subject,
                ["focus"] = focus,
            });

        // Act
        SmartCompartmentMembershipContext membership = SmartCompartmentMembershipContextFactory.Create(
            Expression.SmartCompartmentSearch("Patient", "patient-a", "Observation"),
            rewriter,
            CreateSmartRewriter());

        // Assert
        SmartCompartmentMembershipRule rule = Assert.Single(membership.MembershipRules);
        Assert.Equal("Observation", rule.ResourceType);
        Assert.Equal(subject.Url, Assert.Single(rule.SearchParameterUrls));
        Assert.DoesNotContain(focus.Url, rule.SearchParameterUrls);
    }

    [Fact]
    public void GivenACompartmentParameterWithASiblingReferenceParameter_WhenMembershipCreated_ThenOnlyTheFormalParameterIsUsed()
    {
        // Arrange - Condition-subject is a supported reference parameter targeting Patient, but the Patient
        // CompartmentDefinition nominates only `patient` for Condition. Membership must follow the formal
        // definition and must not absorb sibling parameters, which would widen the compartment.
        var clinicalPatient = new SearchParameterInfo(
            "patient",
            "patient",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/clinical-patient"),
            null,
            "Condition.subject.where(resolve() is Patient)",
            ["Patient"]);
        var subject = new SearchParameterInfo(
            "subject",
            "subject",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/Condition-subject"),
            null,
            "Condition.subject",
            ["Patient"]);
        SqlCompartmentSearchRewriter rewriter = CreateCompartmentRewriter(
            "Condition",
            ["patient"],
            new Dictionary<string, SearchParameterInfo>
            {
                ["patient"] = clinicalPatient,
                ["subject"] = subject,
            });

        // Act
        SmartCompartmentMembershipContext membership = SmartCompartmentMembershipContextFactory.Create(
            Expression.SmartCompartmentSearch("Patient", "patient-a", "Condition"),
            rewriter,
            CreateSmartRewriter());

        // Assert
        SmartCompartmentMembershipRule rule = Assert.Single(membership.MembershipRules);
        Assert.Equal(clinicalPatient.Url, Assert.Single(rule.SearchParameterUrls));
        Assert.DoesNotContain(subject.Url, rule.SearchParameterUrls);
    }

    [Fact]
    public void GivenClinicalPatientParameter_WhenMembershipCreated_ThenFormalParameterIsUsed()
    {
        // Arrange
        var clinicalPatient = new SearchParameterInfo(
            "patient",
            "patient",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/clinical-patient"),
            null,
            "AllergyIntolerance.patient",
            ["Patient"]);
        SqlCompartmentSearchRewriter rewriter = CreateCompartmentRewriter(
            "AllergyIntolerance",
            ["patient"],
            new Dictionary<string, SearchParameterInfo>
            {
                ["patient"] = clinicalPatient,
            });

        // Act
        SmartCompartmentMembershipContext membership = SmartCompartmentMembershipContextFactory.Create(
            Expression.SmartCompartmentSearch("Patient", "patient-a", "AllergyIntolerance"),
            rewriter,
            CreateSmartRewriter());

        // Assert
        SmartCompartmentMembershipRule rule = Assert.Single(membership.MembershipRules);
        Assert.Equal(clinicalPatient.Url, Assert.Single(rule.SearchParameterUrls));
    }

    [Fact]
    public void GivenPractitionerCompartmentEncounter_WhenMembershipCreated_ThenOnlyTheFormalParameterIsUsed()
    {
        // Arrange - Encounter-participant is a supported reference parameter targeting Practitioner, but the
        // Practitioner CompartmentDefinition nominates only `practitioner` for Encounter. Membership must
        // follow the formal definition; absorbing `participant` would also admit RelatedPerson-keyed rows.
        var practitioner = new SearchParameterInfo(
            "practitioner",
            "practitioner",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/Encounter-practitioner"),
            null,
            "Encounter.participant.individual.where(resolve() is Practitioner)",
            ["Practitioner"]);
        var participant = new SearchParameterInfo(
            "participant",
            "participant",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/Encounter-participant"),
            null,
            "Encounter.participant.individual",
            ["Practitioner", "RelatedPerson"]);
        SqlCompartmentSearchRewriter rewriter = CreateCompartmentRewriter(
            "Encounter",
            ["practitioner"],
            new Dictionary<string, SearchParameterInfo>
            {
                ["practitioner"] = practitioner,
                ["participant"] = participant,
            },
            CompartmentType.Practitioner);

        // Act
        SmartCompartmentMembershipContext membership = SmartCompartmentMembershipContextFactory.Create(
            Expression.SmartCompartmentSearch("Practitioner", "practitioner-a", "Encounter"),
            rewriter,
            CreateSmartRewriter());

        // Assert
        SmartCompartmentMembershipRule rule = Assert.Single(membership.MembershipRules);
        Assert.Equal("Encounter", rule.ResourceType);
        Assert.Equal(practitioner.Url, Assert.Single(rule.SearchParameterUrls));
        Assert.DoesNotContain(participant.Url, rule.SearchParameterUrls);
    }

    [Fact]
    public void GivenEpisodeOfCareCareManager_WhenPractitionerMembershipCreated_ThenFormalParameterIsUsed()
    {
        // Arrange - EpisodeOfCare-care-manager is the sole Practitioner compartment parameter for
        // EpisodeOfCare. It is resolve()-based, which the indexer evaluates via
        // LightweightReferenceToElementResolver, so it is materialized like any other reference parameter.
        var careManager = new SearchParameterInfo(
            "care-manager",
            "care-manager",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/EpisodeOfCare-care-manager"),
            null,
            "EpisodeOfCare.careManager.where(resolve() is Practitioner)",
            ["Practitioner"]);
        SqlCompartmentSearchRewriter rewriter = CreateCompartmentRewriter(
            "EpisodeOfCare",
            ["care-manager"],
            new Dictionary<string, SearchParameterInfo>
            {
                ["care-manager"] = careManager,
            },
            CompartmentType.Practitioner);

        // Act
        SmartCompartmentMembershipContext membership = SmartCompartmentMembershipContextFactory.Create(
            Expression.SmartCompartmentSearch("Practitioner", "practitioner-a", "EpisodeOfCare"),
            rewriter,
            CreateSmartRewriter());

        // Assert
        SmartCompartmentMembershipRule rule = Assert.Single(membership.MembershipRules);
        Assert.Equal(careManager.Url, Assert.Single(rule.SearchParameterUrls));
    }

    [Fact]
    public void GivenSmartCompartmentExpressionInsideAndTree_WhenMembershipCreatedAndSqlGenerated_ThenIncludeCandidatePredicateIsEmitted()
    {
        // Canary for the SMART include enforcement chain. SqlServerSearchService derives the membership
        // context by locating the SmartCompartmentSearchExpression inside the core expression tree and
        // attaching the result to the SqlRootExpression; the query generator emits the candidate
        // authorization predicate only when that context is present. If any link breaks — the compartment
        // expression gets wrapped in a node the factory cannot traverse, a rewriter drops the attached
        // context, or the generator stops honoring it — include CTEs silently revert to the pre-fix
        // cross-compartment leak. This test exercises the factory -> attach -> generate chain end to end
        // with the same tree shape SearchOptionsFactory produces (smart node as a child of the top-level And).
        var includeParameterUrl = new Uri("http://hl7.org/fhir/SearchParameter/Observation-subject");
        var membershipParameterUrl = new Uri("http://hl7.org/fhir/SearchParameter/DiagnosticReport-subject");
        var membershipParameter = new SearchParameterInfo(
            "subject",
            "subject",
            SearchParamType.Reference,
            membershipParameterUrl,
            null,
            "DiagnosticReport.subject",
            ["Patient"]);
        SqlCompartmentSearchRewriter rewriter = CreateCompartmentRewriter(
            "DiagnosticReport",
            ["subject"],
            new Dictionary<string, SearchParameterInfo>
            {
                ["subject"] = membershipParameter,
            });

        Expression coreExpression = Expression.And(
            new SearchParameterExpression(new SearchParameterInfo("_type", "_type"), new StringExpression(StringOperator.Equals, FieldName.String, null, "Observation", false)),
            Expression.SmartCompartmentSearch("Patient", "patient-a", "Observation"));

        SmartCompartmentMembershipContext membership = SmartCompartmentMembershipContextFactory.Create(coreExpression, rewriter, CreateSmartRewriter());

        Assert.NotNull(membership);
        Assert.Equal("Patient", membership.CompartmentResourceType);
        Assert.Equal("patient-a", membership.CompartmentResourceId);
        SmartCompartmentMembershipRule rule = Assert.Single(membership.MembershipRules);
        Assert.Equal("DiagnosticReport", rule.ResourceType);
        Assert.Equal(membershipParameterUrl, Assert.Single(rule.SearchParameterUrls));

        var includeParameter = new SearchParameterInfo(
            "subject",
            "subject",
            SearchParamType.Reference,
            includeParameterUrl,
            null,
            "Observation.subject",
            ["Patient"]);
        var includeExpression = new IncludeExpression(
            ["Observation"],
            includeParameter,
            "Observation",
            "Patient",
            null,
            false,
            false,
            false);
        SqlRootExpression sqlExpression = new SqlRootExpression(
            [
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.All),
                new SearchParamTableExpression(IncludeQueryGenerator.Instance, includeExpression, SearchParamTableExpressionKind.Include),
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.IncludeLimit),
                new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.IncludeUnionAll),
            ],
            []).WithSmartCompartmentMembership(membership);
        SearchOptions searchOptions = new()
        {
            IncludeCount = 1,
            MaxItemCount = 10,
            Sort = [],
            ResourceVersionTypes = ResourceVersionType.Latest,
        };

        ConfigureResourceTypeIds();
        _fhirModel.GetSearchParamId(includeParameterUrl).Returns((short)40);
        _fhirModel.GetSearchParamId(membershipParameterUrl).Returns((short)41);

        _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        string generatedSql = _strBuilder.ToString();
        Assert.Contains("smartCompartmentMembership.ResourceTypeId = refTarget.ResourceTypeId", generatedSql);
        Assert.Contains("smartCompartmentMembership.ResourceSurrogateId = refTarget.ResourceSurrogateId", generatedSql);
        Assert.Contains("smartCompartmentMembership.BaseUri IS NULL", generatedSql);
        _fhirModel.Received(1).GetSearchParamId(membershipParameterUrl);
    }

    [Fact]
    public void GivenVectorSearch_WhenSqlGenerated_ThenEmitsDistanceAndModelConstraints()
    {
        string generatedSql = GenerateVectorSearchSql();

        Assert.Contains("VECTOR_DISTANCE('cosine', vectorSearch.Embedding, CAST(@", generatedSql);
        Assert.Contains("AS VECTOR(1536)", generatedSql);
        Assert.Contains("vectorSearch.SearchParamId = 41", generatedSql);
        Assert.Contains("vectorSearch.EmbeddingModelId = @", generatedSql);
        Assert.Contains("vectorSearch.Embedding IS NOT NULL", generatedSql);
        Assert.DoesNotContain("[0.25,0.5]", generatedSql);
        Assert.Contains(_sqlCommand.Parameters.Cast<Data.SqlClient.SqlParameter>(), parameter => Equals(parameter.Value, "[0.25,0.5]"));
        Assert.Contains(_sqlCommand.Parameters.Cast<Data.SqlClient.SqlParameter>(), parameter => Equals(parameter.Value, (short)17));
    }

    [Fact]
    public void GivenRestrictedVectorSearch_WhenSqlGenerated_ThenCandidateLimitIsAppliedAfterPredecessorJoin()
    {
        string generatedSql = GenerateVectorSearchSql(withCompartment: true, k: 5);
        string vectorCte = GetCte(generatedSql, "cte1 AS", "cte2 AS");

        Assert.Contains("TOP (", vectorCte);
        Assert.Contains("FROM dbo.VectorSearchParam vectorSearch", vectorCte);
        Assert.Contains("JOIN cte0 predecessor", vectorCte);
        Assert.Contains("vectorSearch.ResourceTypeId = predecessor.T1", vectorCte);
        Assert.Contains("vectorSearch.ResourceSurrogateId = predecessor.Sid1", vectorCte);
        Assert.Equal(1, CountOccurrences(generatedSql, "FROM dbo.VectorSearchParam"));
    }

    [Fact]
    public void GivenChunkedVectorSearch_WhenSqlGenerated_ThenBestChunkIsCollapsedPerResource()
    {
        string generatedSql = GenerateVectorSearchSql();

        Assert.Contains("MIN(VECTOR_DISTANCE(", generatedSql);
        Assert.Contains("GROUP BY vectorSearch.ResourceTypeId, vectorSearch.ResourceSurrogateId", generatedSql);
    }

    [Fact]
    public void GivenVectorSearchWithMinScore_WhenSqlGenerated_ThenDistanceBoundIsApplied()
    {
        string generatedSql = GenerateVectorSearchSql(minScore: 0.75);

        Assert.Contains("HAVING MIN(VECTOR_DISTANCE(", generatedSql);
        Assert.Contains(")) <= @", generatedSql);
        Assert.Contains(_sqlCommand.Parameters.Cast<Data.SqlClient.SqlParameter>(), parameter => Equals(parameter.Value, 0.5d));
    }

    [Fact]
    public void GivenVectorSearch_WhenSqlGenerated_ThenBestMatchesAreOrderedDeterministically()
    {
        string generatedSql = GenerateVectorSearchSql(k: 5);

        Assert.Contains("ORDER BY VectorDistance ASC, vectorSearch.ResourceSurrogateId ASC", generatedSql);
        Assert.True(
            CountOccurrences(generatedSql, "ORDER BY VectorDistance ASC") >= 2,
            $"Expected both the vector candidate CTE and paging CTE to order by distance in:{Environment.NewLine}{generatedSql}");
        Assert.Contains("ORDER BY t.SortValue ASC, t.ResourceSurrogateId ASC", generatedSql);
    }

    [Fact]
    [Trait(Traits.Category, Categories.CompartmentSearch)]
    public void GivenCompartmentRestrictedVectorSearch_WhenSqlGenerated_ThenCompartmentPrecedesVectorDistance()
    {
        string generatedSql = GenerateVectorSearchSql(withCompartment: true);
        int compartmentCteIndex = generatedSql.IndexOf("cte0 AS", StringComparison.Ordinal);
        int vectorCteIndex = generatedSql.IndexOf("cte1 AS", StringComparison.Ordinal);

        Assert.True(compartmentCteIndex >= 0 && compartmentCteIndex < vectorCteIndex);
        Assert.Contains("CompartmentTypeId = @", generatedSql);
        Assert.Contains("ReferenceResourceId = @", generatedSql);
        Assert.Contains("JOIN cte0 predecessor", generatedSql);
    }

    [Fact]
    public void GivenVectorSearchWithoutPredecessor_WhenSqlGenerated_ThenVectorSeekIsResourceTypeScoped()
    {
        string generatedSql = GenerateVectorSearchSql();
        string vectorCte = GetCte(generatedSql, "cte0 AS", "cte1 AS");

        Assert.DoesNotContain("JOIN cte", vectorCte);
        Assert.Contains("vectorSearch.ResourceTypeId = 3", vectorCte);
    }

    [Fact]
    public void GivenApproximateIndexIsDisabled_WhenSqlGenerated_ThenExactSqlIsUsed()
    {
        _vectorSearchCapabilityProvider.SupportsApproximateVectorIndex.Returns(true);

        string generatedSql = GenerateVectorSearchSql(k: 5);
        string vectorCte = GetCte(generatedSql, "cte0 AS", "cte1 AS");
        IReadOnlyList<string> topParameters = GetTopParameterNames(vectorCte);

        Assert.Contains("VECTOR_DISTANCE(", generatedSql);
        Assert.DoesNotContain("WITH APPROXIMATE", generatedSql);
        Assert.DoesNotContain("VECTOR_SEARCH(", generatedSql);
        Assert.Single(topParameters);
        Assert.Equal(5, Convert.ToInt32(_sqlCommand.Parameters[topParameters[0]].Value));
    }

    [Fact]
    public void GivenApproximateIndexIsEnabledAndAvailable_WhenUnrestrictedSqlGenerated_ThenApproximateSqlIsUsed()
    {
        _vectorSearchConfiguration.EnableApproximateVectorIndex = true;
        _vectorSearchCapabilityProvider.SupportsApproximateVectorIndex.Returns(true);

        string generatedSql = GenerateVectorSearchSql(k: 5, minScore: 0.75);
        string vectorCte = GetCte(generatedSql, "cte0 AS", "cte1 AS");

        Assert.Contains("SELECT TOP (", vectorCte);
        Assert.Contains("WITH APPROXIMATE", vectorCte);
        Assert.Contains("FROM VECTOR_SEARCH(", vectorCte);
        Assert.Contains("TABLE = dbo.VectorSearchParam AS vectorEntry", vectorCte);
        Assert.Contains("ORDER BY vectorResult.distance ASC", vectorCte);
        Assert.DoesNotContain("ORDER BY vectorResult.distance ASC,", vectorCte);
        Assert.Contains("vectorEntry.EmbeddingModelId = @", vectorCte);
        Assert.Contains("MIN(vectorSearch.distance) AS VectorDistance", vectorCte);
        Assert.Contains("GROUP BY vectorSearch.ResourceTypeId, vectorSearch.ResourceSurrogateId", vectorCte);
        Assert.Contains("HAVING MIN(vectorSearch.distance) <= @", vectorCte);

        IReadOnlyList<string> topParameters = GetTopParameterNames(vectorCte);
        Assert.Equal(2, topParameters.Count);
        Assert.NotEqual(topParameters[0], topParameters[1]);
        int outerTop = Convert.ToInt32(_sqlCommand.Parameters[topParameters[0]].Value);
        int innerTop = Convert.ToInt32(_sqlCommand.Parameters[topParameters[1]].Value);
        Assert.True(innerTop > outerTop, $"Expected inner ANN budget {innerTop} to exceed outer resource limit {outerTop}.");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void GivenApproximateIndexAndNonIncreasingOversampling_WhenSqlGenerated_ThenInnerBudgetIsNotLessThanK(double oversamplingFactor)
    {
        _vectorSearchConfiguration.EnableApproximateVectorIndex = true;
        _vectorSearchConfiguration.OversamplingFactor = oversamplingFactor;
        _vectorSearchCapabilityProvider.SupportsApproximateVectorIndex.Returns(true);

        string generatedSql = GenerateVectorSearchSql(k: 5);
        string vectorCte = GetCte(generatedSql, "cte0 AS", "cte1 AS");
        IReadOnlyList<string> topParameters = GetTopParameterNames(vectorCte);

        Assert.Equal(2, topParameters.Count);
        int outerTop = Convert.ToInt32(_sqlCommand.Parameters[topParameters[0]].Value);
        int innerTop = Convert.ToInt32(_sqlCommand.Parameters[topParameters[1]].Value);
        Assert.True(innerTop >= outerTop, $"Expected inner ANN budget {innerTop} to be at least outer resource limit {outerTop}.");
    }

    [Fact]
    public void GivenApproximateIndexAndHugeOversampling_WhenSqlGenerated_ThenInnerBudgetIsClampedWithoutOverflow()
    {
        _vectorSearchConfiguration.EnableApproximateVectorIndex = true;
        _vectorSearchConfiguration.OversamplingFactor = double.MaxValue;
        _vectorSearchCapabilityProvider.SupportsApproximateVectorIndex.Returns(true);

        string generatedSql = GenerateVectorSearchSql(k: int.MaxValue);
        string vectorCte = GetCte(generatedSql, "cte0 AS", "cte1 AS");
        IReadOnlyList<string> topParameters = GetTopParameterNames(vectorCte);

        Assert.Equal(2, topParameters.Count);
        Assert.Equal(int.MaxValue, Convert.ToInt32(_sqlCommand.Parameters[topParameters[0]].Value));
        Assert.Equal(int.MaxValue, Convert.ToInt32(_sqlCommand.Parameters[topParameters[1]].Value));
    }

    [Fact]
    public void GivenApproximateIndexIsEnabledButUnavailable_WhenSqlGenerated_ThenExactSqlIsUsed()
    {
        _vectorSearchConfiguration.EnableApproximateVectorIndex = true;
        _vectorSearchCapabilityProvider.SupportsApproximateVectorIndex.Returns(false);

        string generatedSql = GenerateVectorSearchSql(k: 5);

        Assert.Contains("VECTOR_DISTANCE(", generatedSql);
        Assert.DoesNotContain("WITH APPROXIMATE", generatedSql);
        Assert.DoesNotContain("VECTOR_SEARCH(", generatedSql);
    }

    [Fact]
    [Trait(Traits.Category, Categories.CompartmentSearch)]
    public void GivenApproximateIndexIsEnabledAndAvailable_WhenPredecessorExists_ThenExactSqlIsUsed()
    {
        _vectorSearchConfiguration.EnableApproximateVectorIndex = true;
        _vectorSearchCapabilityProvider.SupportsApproximateVectorIndex.Returns(true);

        string generatedSql = GenerateVectorSearchSql(withCompartment: true, k: 5);
        string vectorCte = GetCte(generatedSql, "cte1 AS", "cte2 AS");

        Assert.Contains("JOIN cte0 predecessor", vectorCte);
        Assert.Contains("VECTOR_DISTANCE(", vectorCte);
        Assert.DoesNotContain("WITH APPROXIMATE", vectorCte);
        Assert.DoesNotContain("VECTOR_SEARCH(", vectorCte);
    }

    private string GenerateVectorSearchSql(bool withCompartment = false, int? k = null, double? minScore = null)
    {
        var vectorParameterUrl = new Uri("http://example.org/SearchParameter/Observation-clinical-text");
        var vectorParameter = new SearchParameterInfo(
            "clinical-text",
            "clinical-text",
            SearchParamType.String,
            vectorParameterUrl,
            expression: "Observation.note.text",
            baseResourceTypes: ["Observation"])
        {
            VectorConfig = new VectorSearchParameterConfig(),
        };
        var vectorExpression = new VectorSearchExpression(vectorParameter, "chest pain", k, minScore);
        vectorExpression.SetQueryEmbedding([0.25f, 0.5f]);
        var searchParameterExpression = new SearchParameterExpression(vectorParameter, vectorExpression);

        _fhirModel.GetSearchParamId(vectorParameterUrl).Returns((short)41);
        _fhirModel.GetResourceTypeId("Observation").Returns((short)3);

        var tableExpressions = new List<SearchParamTableExpression>();
        if (withCompartment)
        {
            _fhirModel.GetCompartmentTypeId("Patient").Returns((byte)1);
            tableExpressions.Add(new SearchParamTableExpression(
                CompartmentQueryGenerator.Instance,
                new CompartmentSearchExpression("Patient", "patient-a"),
                SearchParamTableExpressionKind.Normal));
        }

        tableExpressions.Add(new SearchParamTableExpression(
            searchParameterExpression.AcceptVisitor(_queryGeneratorFactory, null),
            searchParameterExpression,
            SearchParamTableExpressionKind.Normal));
        tableExpressions.Add(new SearchParamTableExpression(null, null, SearchParamTableExpressionKind.Top));

        var sqlExpression = new SqlRootExpression(tableExpressions, []);
        var searchOptions = new SearchOptions
        {
            MaxItemCount = 10,
            Sort =
            [
                (SearchParameterInfo.ResourceTypeSearchParameter, SortOrder.Ascending),
                (new SearchParameterInfo(SearchParameterNames.LastUpdated, SearchParameterNames.LastUpdated), SortOrder.Ascending),
            ],
            ResourceVersionTypes = ResourceVersionType.Latest,
        };

        _queryGenerator.VisitSqlRoot(sqlExpression, searchOptions);

        return _strBuilder.ToString();
    }

    private static string GetCte(string sql, string startMarker, string endMarker)
    {
        int start = sql.IndexOf(startMarker, StringComparison.Ordinal);
        int end = sql.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Could not find CTE marker '{startMarker}' in:{Environment.NewLine}{sql}");
        Assert.True(end > start, $"Could not find CTE marker '{endMarker}' after '{startMarker}' in:{Environment.NewLine}{sql}");

        return sql[start..end];
    }

    private static int CountOccurrences(string value, string expected)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += expected.Length;
        }

        return count;
    }

    private void ConfigureResourceTypeIds()
    {
        var resourceTypeIds = new Dictionary<string, short>(StringComparer.Ordinal)
        {
            ["Patient"] = 1,
            ["Practitioner"] = 2,
            ["Observation"] = 3,
            ["DiagnosticReport"] = 4,
        };

        _fhirModel.TryGetResourceTypeId(Arg.Any<string>(), out Arg.Any<short>())
            .Returns(call =>
            {
                call[1] = resourceTypeIds[(string)call[0]];
                return true;
            });
    }

    private static SqlCompartmentSearchRewriter CreateCompartmentRewriter(
        string resourceType,
        HashSet<string> compartmentParameterCodes,
        IReadOnlyDictionary<string, SearchParameterInfo> searchParameters,
        CompartmentType compartmentType = CompartmentType.Patient)
    {
        ICompartmentDefinitionManager compartmentDefinitionManager = Substitute.For<ICompartmentDefinitionManager>();
        compartmentDefinitionManager.TryGetResourceTypes(compartmentType, out Arg.Any<HashSet<string>>())
            .Returns(call =>
            {
                call[1] = new HashSet<string>(StringComparer.Ordinal) { resourceType };
                return true;
            });
        compartmentDefinitionManager.TryGetSearchParams(resourceType, compartmentType, out Arg.Any<HashSet<string>>())
            .Returns(call =>
            {
                call[2] = compartmentParameterCodes;
                return true;
            });

        ISearchParameterDefinitionManager searchParameterDefinitionManager = Substitute.For<ISearchParameterDefinitionManager>();
        searchParameterDefinitionManager.TryGetSearchParameter(resourceType, Arg.Any<string>(), out Arg.Any<SearchParameterInfo>())
            .Returns(call =>
            {
                bool found = searchParameters.TryGetValue((string)call[1], out SearchParameterInfo parameter);
                call[2] = parameter;
                return found;
            });

        return new SqlCompartmentSearchRewriter(
            new Lazy<ICompartmentDefinitionManager>(() => compartmentDefinitionManager),
            new Lazy<ISearchParameterDefinitionManager>(() => searchParameterDefinitionManager));
    }

    // Production always supplies a SmartCompartmentSearchRewriter to the factory, so the parameter is
    // required. The SMART Device conditional-visibility rules are exercised end to end in
    // SmartCompartmentSearchRewriterTests; these membership tests assert only formal compartment
    // membership parameters, so the Device restriction is disabled here to keep the conditional-rule
    // set empty (preserving each test's original configuration and expectations).
    private static SmartCompartmentSearchRewriter CreateSmartRewriter()
    {
        ISearchParameterDefinitionManager searchParameterDefinitionManager = Substitute.For<ISearchParameterDefinitionManager>();
        ICompartmentDefinitionManager compartmentDefinitionManager = Substitute.For<ICompartmentDefinitionManager>();

        var compartmentRewriter = new SqlCompartmentSearchRewriter(
            new Lazy<ICompartmentDefinitionManager>(() => compartmentDefinitionManager),
            new Lazy<ISearchParameterDefinitionManager>(() => searchParameterDefinitionManager));

        return new SmartCompartmentSearchRewriter(
            compartmentRewriter,
            new Lazy<ISearchParameterDefinitionManager>(() => searchParameterDefinitionManager),
            Options.Create(new CoreFeatureConfiguration { EnableSmartCompartmentDeviceRestriction = false }));
    }

    private static IReadOnlyList<string> GetTopParameterNames(string sql)
    {
        const string marker = "TOP (";
        var parameterNames = new List<string>();
        int searchStart = 0;

        while ((searchStart = sql.IndexOf(marker, searchStart, StringComparison.Ordinal)) >= 0)
        {
            int parameterStart = searchStart + marker.Length;
            int parameterEnd = sql.IndexOf(')', parameterStart);
            Assert.True(parameterEnd > parameterStart, $"Could not parse TOP parameter in:{Environment.NewLine}{sql}");
            parameterNames.Add(sql[parameterStart..parameterEnd]);
            searchStart = parameterEnd + 1;
        }

        return parameterNames;
    }
}
