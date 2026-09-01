// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Definition.BundleWrappers;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Converters;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.IndexAndReindex)]
    [Trait(Traits.Category, Categories.Search)]
    public class TypedElementSearchIndexerTests
    {
        private readonly ISearchIndexer _searchIndexer;

        private static readonly string ResourceStaus = "http://hl7.org/fhir/SearchParameter/Resource-status";
        private static readonly string ResourceUse = "http://hl7.org/fhir/SearchParameter/Resource-use";
        private static readonly string ResourceName = "http://hl7.org/fhir/SearchParameter/name";

        private const string CoverageStausExpression = "Coverage.status";
        private const string ObservationStausExpression = "Observation.status";
        private const string ClaimUseExpression = "Claim.use";
        private const string PatientNameExpression = "Patient.name";

        private static SearchParameterInfo statusSearchParameterInfo;

        public TypedElementSearchIndexerTests()
        {
            var supportedSearchParameterDefinitionManager = Substitute.For<ISupportedSearchParameterDefinitionManager>();
            var typedElementToSearchValueConverterManager = GetTypeConverterAsync().Result;
            var referenceToElementResolver = Substitute.For<IReferenceToElementResolver>();
            var modelInfoProvider = ModelInfoProvider.Instance;
            var logger = Substitute.For<ILogger<TypedElementSearchIndexer>>();

            _searchIndexer = new TypedElementSearchIndexer(supportedSearchParameterDefinitionManager, typedElementToSearchValueConverterManager, referenceToElementResolver, modelInfoProvider, logger);

            List<string> baseResourceTypes = new List<string>() { "Resource" };
            List<string> targetResourceTypes = new List<string>() { "Coverage", "Observation", "Claim", "Patient" };
            statusSearchParameterInfo = new SearchParameterInfo("_status", "_status", (ValueSets.SearchParamType)SearchParamType.Token, new Uri(ResourceStaus), expression: CoverageStausExpression, targetResourceTypes: targetResourceTypes, baseResourceTypes: baseResourceTypes);
            var searchParameterInfos = new[]
            {
                statusSearchParameterInfo,
                new SearchParameterInfo("_status", "_status", (ValueSets.SearchParamType)SearchParamType.Token, new Uri(ResourceStaus), expression: ObservationStausExpression, targetResourceTypes: targetResourceTypes, baseResourceTypes: baseResourceTypes),
                new SearchParameterInfo("_use", "_use", (ValueSets.SearchParamType)SearchParamType.Token, new Uri(ResourceUse), expression: ClaimUseExpression, targetResourceTypes: targetResourceTypes, baseResourceTypes: baseResourceTypes),
                new SearchParameterInfo("name", "name", (ValueSets.SearchParamType)SearchParamType.String, new Uri(ResourceName), expression: PatientNameExpression, targetResourceTypes: targetResourceTypes, baseResourceTypes: baseResourceTypes),
            };
            supportedSearchParameterDefinitionManager.GetSearchParameters(Arg.Any<string>()).Returns(searchParameterInfos);
        }

        protected async Task<ITypedElementToSearchValueConverterManager> GetTypeConverterAsync()
        {
            FhirTypedElementToSearchValueConverterManager fhirTypedElementToSearchValueConverterManager = await SearchParameterFixtureData.GetFhirTypedElementToSearchValueConverterManagerAsync();
            return fhirTypedElementToSearchValueConverterManager;
        }

        [Fact]
        public void GivenAValidResource_WhenExtract_ThenValidSearchIndexEntriesAreCreated()
        {
            var coverageResource = Samples.GetDefaultCoverage().ToPoco<Coverage>();

            var searchIndexEntry = _searchIndexer.Extract(coverageResource.ToResourceElement());
            Assert.NotEmpty(searchIndexEntry);

            var tokenSearchValue = searchIndexEntry.First().Value as TokenSearchValue;
            Assert.NotNull(tokenSearchValue);

            Assert.True(coverageResource.Status.Value.ToString().Equals(tokenSearchValue.Code, StringComparison.CurrentCultureIgnoreCase));
        }

        [Fact]
        public void GivenAValidResourceWithDuplicateSearchIndices_WhenExtract_ThenDistincSearchIndexEntriesAreCreated()
        {
            var patientResource = Samples.GetDefaultPatient().ToPoco<Patient>();
            var familyName = "Chalmers";
            var nameList = new List<HumanName>()
            {
                new HumanName() { Use = HumanName.NameUse.Official, Family = familyName },
                new HumanName() { Use = HumanName.NameUse.Official, Family = familyName },
            };
            patientResource.Name = nameList;

            var serachIndexEntry = _searchIndexer.Extract(patientResource.ToResourceElement());
            Assert.Single(serachIndexEntry);

            var nameSearchValue = serachIndexEntry.First().Value as StringSearchValue;
            Assert.Equal(familyName, nameSearchValue.String);
         }

#if !Stu3
        [Fact]
        public void GivenObservationWithTwoNotesAndConcatenateVectorPolicy_WhenExtract_ThenCreatesSingleCombinedVectorValue()
        {
           var extraction = ExtractVectorSearchEntries(VectorTextExtractionPolicy.Concatenate, 10, "first note", "second note");

           var entry = Assert.Single(extraction.Entries);
           var value = Assert.IsType<VectorSearchValue>(entry.Value);
           Assert.Equal("first note\nsecond note", value.SourceText);
           Assert.Equal(0, value.ChunkOrdinal);
        }

        [Fact]
        public void GivenObservationWithTwoNotesAndPerValueRowVectorPolicy_WhenExtract_ThenCreatesVectorValueForEachNote()
        {
           var extraction = ExtractVectorSearchEntries(VectorTextExtractionPolicy.PerValueRow, 10, "first note", "second note");

           var values = extraction.Entries
               .Select(entry => Assert.IsType<VectorSearchValue>(entry.Value))
               .ToList();

           Assert.Collection(
               values,
               value =>
               {
                   Assert.Equal("first note", value.SourceText);
                   Assert.Equal(0, value.ChunkOrdinal);
               },
               value =>
               {
                   Assert.Equal("second note", value.SourceText);
                   Assert.Equal(1, value.ChunkOrdinal);
               });
        }

        [Fact]
        public void GivenObservationWithTwoNotesAndFirstValueVectorPolicy_WhenExtract_ThenCreatesVectorValueForFirstNoteOnly()
        {
           var extraction = ExtractVectorSearchEntries(VectorTextExtractionPolicy.FirstValue, 10, "first note", "second note");

           var entry = Assert.Single(extraction.Entries);
           var value = Assert.IsType<VectorSearchValue>(entry.Value);
           Assert.Equal("first note", value.SourceText);
           Assert.Equal(0, value.ChunkOrdinal);
        }

        [Fact]
        public void GivenObservationWithWhitespaceOnlyNotesAndVectorPolicy_WhenExtract_ThenCreatesNoEntriesWithoutUsingConverter()
        {
           var extraction = ExtractVectorSearchEntries(VectorTextExtractionPolicy.Concatenate, 10, " ", "\t");

           Assert.Empty(extraction.Entries);
           extraction.ConverterManager.DidNotReceiveWithAnyArgs().TryGetConverter(default, default, out _);
        }

        [Fact]
        public void GivenObservationWithLongNotesAndConcatenateVectorPolicy_WhenExtract_ThenTruncatesJoinedTextToCharacterBudget()
        {
           var extraction = ExtractVectorSearchEntries(VectorTextExtractionPolicy.Concatenate, 3, "1234567890", "abcdefghij");

           var entry = Assert.Single(extraction.Entries);
           var value = Assert.IsType<VectorSearchValue>(entry.Value);
           Assert.Equal("1234567890\na", value.SourceText);
           Assert.Equal(12, value.SourceText.Length);
        }

        [Fact]
        public void GivenOrdinarySearchParameter_WhenExtract_ThenUsesNormalConverterPath()
        {
           var observationResource = new Observation
           {
               Status = ObservationStatus.Final,
           };

           var entries = _searchIndexer.Extract(observationResource.ToResourceElement());

           var entry = Assert.Single(entries);
           Assert.IsType<TokenSearchValue>(entry.Value);
        }

        private (IReadOnlyCollection<SearchIndexEntry> Entries, ITypedElementToSearchValueConverterManager ConverterManager) ExtractVectorSearchEntries(
           VectorTextExtractionPolicy extractionPolicy,
           int maxInputTokens,
           params string[] noteTexts)
        {
           var supportedSearchParameterDefinitionManager = Substitute.For<ISupportedSearchParameterDefinitionManager>();
           var typedElementToSearchValueConverterManager = Substitute.For<ITypedElementToSearchValueConverterManager>();
           var referenceToElementResolver = Substitute.For<IReferenceToElementResolver>();
           var logger = Substitute.For<ILogger<TypedElementSearchIndexer>>();
           var searchParameter = new SearchParameterInfo(
               "vector-note",
               "vector-note",
               (ValueSets.SearchParamType)SearchParamType.String,
               new Uri("http://example.org/SearchParameter/vector-note"),
               expression: "Observation.note.text",
               targetResourceTypes: new[] { "Observation" },
               baseResourceTypes: new[] { "Observation" })
           {
               VectorConfig = new VectorSearchParameterConfig
               {
                   ExtractionPolicy = extractionPolicy,
                   MaxInputTokens = maxInputTokens,
               },
           };
           supportedSearchParameterDefinitionManager.GetSearchParameters("Observation").Returns(new[] { searchParameter });

           var indexer = new TypedElementSearchIndexer(
               supportedSearchParameterDefinitionManager,
               typedElementToSearchValueConverterManager,
               referenceToElementResolver,
               ModelInfoProvider.Instance,
               logger);
           var observationResource = new Observation
           {
               Note = noteTexts.Select(text => new Annotation { Text = text }).ToList(),
           };

           return (indexer.Extract(observationResource.ToResourceElement()), typedElementToSearchValueConverterManager);
        }
#endif

#if !Stu3
        // For Stu3 - Coverage.status, Observation.status, and Claim.use are not required fields
        [Fact]
        public void GivenAnValidResource_WhenExtract_ThenExceptionIsNotThrown()
        {
            var requestBundle = Samples.GetJsonSample("Bundle-TransactionWithInvalidBundleEntry");

            foreach (var entry in new BundleWrapper(requestBundle.Instance).Entries)
            {
                ResourceElement resourceElement = null;
                string errorMessage = null;
                switch (entry.Resource.InstanceType)
                {
                    case "Coverage":
                        resourceElement = entry.Resource.ToPoco<Coverage>().ToResourceElement();
                        errorMessage = string.Format(Core.Resources.ValueCannotBeNull, CoverageStausExpression);
                        break;
                    case "Observation":
                        resourceElement = entry.Resource.ToPoco<Observation>().ToResourceElement();
                        errorMessage = string.Format(Core.Resources.ValueCannotBeNull, ObservationStausExpression);
                        break;
                    case "Claim":
                        resourceElement = entry.Resource.ToPoco<Claim>().ToResourceElement();
                        errorMessage = string.Format(Core.Resources.ValueCannotBeNull, ClaimUseExpression);
                        break;
                    default: break;
                }

                var exception = Record.Exception(() => _searchIndexer.Extract(resourceElement));
                Assert.Null(exception);
            }
        }
#endif
    }
}
