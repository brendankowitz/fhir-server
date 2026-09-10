// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.Health.Extensions.Xunit;
using Microsoft.Health.Fhir.Client;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Test.Utilities;
using Newtonsoft.Json.Linq;
using Xunit;
using Task = System.Threading.Tasks.Task;

#if !Stu3
namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Search
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Search)]
    [HttpIntegrationFixtureArgumentSets(DataStore.SqlServer, Format.Json)]
    public class VectorSearchTests : SearchTestsBase<HttpIntegrationTestFixture>
    {
        private const string VectorConfigExtensionUrl = "http://microsoft.com/fhir/StructureDefinition/vector-search-config";
        private const string EvidenceExtensionUrl = "http://microsoft.com/fhir/StructureDefinition/semantic-search-evidence";
        private const string EnabledEnvironmentVariable = "FHIR_SERVER_VECTOR_SEARCH_E2E_ENABLED";
        private const string DeterministicEnvironmentVariable = "FHIR_SERVER_VECTOR_SEARCH_E2E_DETERMINISTIC";
        private const string AsyncEnvironmentVariable = "FHIR_SERVER_VECTOR_SEARCH_E2E_ASYNC_ENABLED";
        private const string DisabledEnvironmentVariable = "FHIR_SERVER_VECTOR_SEARCH_DISABLED_E2E_ENABLED";
        private static readonly TimeSpan IndexingTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        public VectorSearchTests(HttpIntegrationTestFixture fixture)
            : base(fixture)
        {
        }

        [SkippableFact]
        public async Task GivenAVectorSearchParameterAndResource_WhenSearchingRelatedText_ThenResourceAndScoreAreReturned()
        {
            RequireEnabledDeterministicServer();
            string suffix = CreateSuffix();
            SearchParameter searchParameter = await CreateVectorSearchParameterAsync(suffix);
            Observation observation = await CreateObservationAsync($"A cyclist developed exertional shortness of breath. {suffix}");

            Bundle bundle = await WaitForSearchAsync(
                BuildQuery(searchParameter.Code, $"difficulty breathing during exercise {suffix}"),
                result => ContainsResource(result, observation.Id));

            Bundle.EntryComponent entry = Assert.Single(bundle.Entry, candidate => candidate.Resource?.Id == observation.Id);
            Assert.InRange(entry.Search.Score.Value, 0m, 1m);
        }

        [SkippableFact]
        public async Task GivenAVectorSearchResult_WhenReadFromJson_ThenScoreSurvivesServerSerialization()
        {
            RequireEnabledDeterministicServer();
            string suffix = CreateSuffix();
            SearchParameter searchParameter = await CreateVectorSearchParameterAsync(suffix);
            Observation observation = await CreateObservationAsync($"Serialization sentinel passage {suffix}");
            string query = BuildQuery(searchParameter.Code, $"Serialization sentinel passage {suffix}");

            await WaitForSearchAsync(query, result => ContainsResource(result, observation.Id));

            using HttpResponseMessage response = await Fixture.HttpClient.GetAsync(Fixture.GenerateFullUrl(query));
            string json = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            JObject bundle = JObject.Parse(json);
            JToken entry = Assert.Single(bundle["entry"], candidate => candidate["resource"]?["id"]?.Value<string>() == observation.Id);
            decimal score = entry["search"]?["score"]?.Value<decimal>() ?? throw new Xunit.Sdk.XunitException("The serialized bundle entry did not contain search.score.");
            Assert.InRange(score, 0m, 1m);
        }

        [SkippableFact]
        public async Task GivenAVectorSearchResult_WhenEvidenceIsReturned_ThenItContainsTheMatchedPassage()
        {
            RequireEnabledDeterministicServer();
            string suffix = CreateSuffix();
            SearchParameter searchParameter = await CreateVectorSearchParameterAsync(suffix);
            string passage = $"The patient reports a persistent nocturnal cough. {suffix}";
            Observation observation = await CreateObservationAsync(passage);

            Bundle bundle = await WaitForSearchAsync(
                BuildQuery(searchParameter.Code, $"coughing at night {suffix}"),
                result => ContainsResource(result, observation.Id));

            Bundle.EntryComponent entry = Assert.Single(bundle.Entry, candidate => candidate.Resource?.Id == observation.Id);
            Extension evidence = Assert.Single(entry.Search.Extension, extension => extension.Url == EvidenceExtensionUrl);
            Extension text = Assert.Single(evidence.Extension, extension => extension.Url == "text");
            Assert.Contains(passage, Assert.IsType<FhirString>(text.Value).Value, StringComparison.Ordinal);
        }

        [SkippableFact]
        public async Task GivenChangedVectorSourceText_WhenWatchdogRuns_ThenOldVectorIsReplacedByNewVector()
        {
            RequireEnabledDeterministicServer();
            Skip.IfNot(IsTrue(AsyncEnvironmentVariable), $"Set {AsyncEnvironmentVariable}=true only for a server using asynchronous persistence with the vectorization watchdog enabled.");
            string suffix = CreateSuffix();
            SearchParameter searchParameter = await CreateVectorSearchParameterAsync(suffix);
            string oldText = $"fractured left wrist {suffix}";
            string newText = $"seasonal allergic rhinitis {suffix}";
            Observation observation = await CreateObservationAsync(oldText);

            await WaitForSearchAsync(BuildQuery(searchParameter.Code, oldText, minimumScore: 0.99m), result => ContainsResource(result, observation.Id));
            observation.Note[0].Text = newText;
            using FhirResponse<Observation> updateResponse = await Client.UpdateAsync(observation);

            await WaitForSearchAsync(
                BuildQuery(searchParameter.Code, newText, minimumScore: 0.99m),
                result => ContainsResource(result, observation.Id));
            Bundle oldResult = await Client.SearchAsync(BuildQuery(searchParameter.Code, oldText, minimumScore: 0.99m));
            Assert.DoesNotContain(oldResult.Entry, entry => entry.Resource?.Id == observation.Id);
        }

        [SkippableFact]
        public async Task GivenACompartmentRestrictedVectorSearch_WhenCloserResourceIsOutsideCompartment_ThenItIsNotReturned()
        {
            RequireEnabledDeterministicServer();
            string suffix = CreateSuffix();
            SearchParameter searchParameter = await CreateVectorSearchParameterAsync(suffix);
            Patient allowedPatient = await CreatePatientAsync();
            Patient excludedPatient = await CreatePatientAsync();
            Observation allowed = await CreateObservationAsync($"difficulty breathing after exertion {suffix}", allowedPatient);
            Observation excluded = await CreateObservationAsync($"acute respiratory distress {suffix}", excludedPatient);
            string queryText = $"acute respiratory distress {suffix}";

            Bundle unrestricted = await WaitForSearchAsync(
                BuildQuery(searchParameter.Code, queryText, k: 2),
                result => ContainsResource(result, allowed.Id) && ContainsResource(result, excluded.Id));
            Assert.Equal(excluded.Id, unrestricted.Entry.First(entry => entry.Search?.Mode == Bundle.SearchEntryMode.Match).Resource.Id);

            Bundle restricted = await Client.SearchAsync(
                $"Patient/{allowedPatient.Id}/Observation?{searchParameter.Code}={Uri.EscapeDataString(queryText)}&_vectorK=2");
            Assert.Contains(restricted.Entry, entry => entry.Resource?.Id == allowed.Id);
            Assert.DoesNotContain(restricted.Entry, entry => entry.Resource?.Id == excluded.Id);
        }

        [SkippableFact]
        public async Task GivenVectorKAndMinimumScore_WhenSearching_ThenCountAndThresholdAreApplied()
        {
            RequireEnabledDeterministicServer();
            string suffix = CreateSuffix();
            SearchParameter searchParameter = await CreateVectorSearchParameterAsync(suffix);
            string exactText = $"unique exact threshold phrase {suffix}";
            Observation exact = await CreateObservationAsync(exactText);
            await CreateObservationAsync($"related threshold phrase {suffix}");
            await CreateObservationAsync($"unrelated control text {suffix}");

            Bundle bounded = await WaitForSearchAsync(
                BuildQuery(searchParameter.Code, exactText, k: 2),
                result => ContainsResource(result, exact.Id));
            Assert.InRange(bounded.Entry.Count(entry => entry.Search?.Mode == Bundle.SearchEntryMode.Match), 1, 2);

            Bundle thresholded = await Client.SearchAsync(BuildQuery(searchParameter.Code, exactText, k: 10, minimumScore: 1m));
            Bundle.EntryComponent entry = Assert.Single(thresholded.Entry, candidate => candidate.Search?.Mode == Bundle.SearchEntryMode.Match);
            Assert.Equal(exact.Id, entry.Resource.Id);
            Assert.Equal(1m, entry.Search.Score);
        }

        [SkippableFact]
        public async Task GivenPagedVectorResults_WhenFollowingNextLinks_ThenEveryResourceIsReturnedExactlyOnce()
        {
            RequireEnabledDeterministicServer();
            string suffix = CreateSuffix();
            SearchParameter searchParameter = await CreateVectorSearchParameterAsync(suffix);
            string passage = $"shared paging passage {suffix}";
            var expected = new HashSet<string>();
            for (int i = 0; i < 5; i++)
            {
                expected.Add((await CreateObservationAsync(passage)).Id);
            }

            string query = BuildQuery(searchParameter.Code, passage, k: 5, minimumScore: 0.99m) + "&_count=2";
            Bundle bundle = await WaitForSearchAsync(query, result => result.Entry.Count > 0);
            var actual = new List<string>();
            while (bundle != null)
            {
                actual.AddRange(bundle.Entry.Where(entry => entry.Search?.Mode == Bundle.SearchEntryMode.Match).Select(entry => entry.Resource.Id));
                bundle = bundle.NextLink == null ? null : await Client.SearchAsync(bundle.NextLink.ToString());
            }

            Assert.Equal(5, actual.Count);
            Assert.Equal(5, actual.Distinct(StringComparer.Ordinal).Count());
            Assert.True(expected.SetEquals(actual));
        }

        [SkippableFact]
        public async Task GivenVectorSearchIsDisabled_WhenQueryingVectorParameter_ThenQueryIsRejectedAsUnsupported()
        {
            Skip.IfNot(IsTrue(DisabledEnvironmentVariable), $"Set {DisabledEnvironmentVariable}=true only for a server started with FhirServer:VectorSearch:Enabled=false.");
            string suffix = CreateSuffix();
            SearchParameter searchParameter = await CreateVectorSearchParameterAsync(suffix);

            using FhirClientException exception = await Assert.ThrowsAsync<FhirClientException>(
                () => Client.SearchAsync(BuildQuery(searchParameter.Code, $"disabled vector query {suffix}")));

            Assert.Contains(exception.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity });
            Assert.Contains(
                exception.OperationOutcome.Issue,
                issue => issue.Diagnostics?.Contains("not supported", StringComparison.OrdinalIgnoreCase) == true);
        }

        private static string CreateSuffix() => Guid.NewGuid().ToString("N");

        private static bool IsTrue(string variableName) =>
            string.Equals(Environment.GetEnvironmentVariable(variableName), "true", StringComparison.OrdinalIgnoreCase);

        private static void RequireEnabledDeterministicServer()
        {
            Skip.IfNot(IsTrue(EnabledEnvironmentVariable), $"Vector search capability is absent. Set {EnabledEnvironmentVariable}=true for an Azure SQL VECTOR test server with vector search enabled.");
            Skip.IfNot(IsTrue(DeterministicEnvironmentVariable), $"Set {DeterministicEnvironmentVariable}=true only when the server embedding deployment is a deterministic test double.");
        }

        private async Task<SearchParameter> CreateVectorSearchParameterAsync(string suffix)
        {
#if R5
            var baseResourceTypes = new List<VersionIndependentResourceTypesAll?> { VersionIndependentResourceTypesAll.Observation };
#else
            var baseResourceTypes = new List<ResourceType?> { ResourceType.Observation };
#endif
            var vectorConfig = new Extension
            {
                Url = VectorConfigExtensionUrl,
                Extension = new List<Extension>
                {
                    new Extension("extractionPolicy", new Code("concatenate")),
                    new Extension("maxInputTokens", new Integer(8000)),
                },
            };
            var searchParameter = new SearchParameter
            {
                Id = $"vector-note-{suffix[..12]}",
                Url = $"http://example.org/fhir/SearchParameter/Observation-vector-note-{suffix}",
                Name = $"VectorNote{suffix[..12]}",
                Code = $"vector-note-{suffix[..12]}",
                Status = PublicationStatus.Active,
                Description = new Markdown("Vector search E2E parameter."),
                Base = baseResourceTypes,
                Type = SearchParamType.Special,
                Expression = "Observation.note.text",
                Extension = new List<Extension> { vectorConfig },
            };

            using FhirResponse<SearchParameter> response = await Client.UpdateAsync(searchParameter);
            return response.Resource;
        }

        private async Task<Observation> CreateObservationAsync(string text, Patient patient = null)
        {
            var observation = new Observation
            {
                Status = ObservationStatus.Final,
                Code = new CodeableConcept("http://loinc.org", "75325-1"),
                Note = new List<Annotation> { new Annotation { Text = text } },
                Subject = patient == null ? null : new ResourceReference($"Patient/{patient.Id}"),
            };
            using FhirResponse<Observation> response = await Client.CreateAsync(observation);
            return response.Resource;
        }

        private async Task<Patient> CreatePatientAsync()
        {
            using FhirResponse<Patient> response = await Client.CreateAsync(new Patient { Active = true });
            return response.Resource;
        }

        private async Task<Bundle> WaitForSearchAsync(string query, Func<Bundle, bool> condition)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + IndexingTimeout;
            Exception lastException = null;
            do
            {
                try
                {
                    Bundle bundle = await Client.SearchAsync(query);
                    if (condition(bundle))
                    {
                        return bundle;
                    }
                }
                catch (FhirClientException exception)
                {
                    lastException = exception;
                }

                await Task.Delay(PollInterval);
            }
            while (DateTimeOffset.UtcNow < deadline);

            throw new TimeoutException($"Vector search did not reach the expected state for '{query}'.", lastException);
        }

        private static bool ContainsResource(Bundle bundle, string id) =>
            bundle.Entry.Any(entry => entry.Search?.Mode == Bundle.SearchEntryMode.Match && entry.Resource?.Id == id);

        private static string BuildQuery(string code, string text, int? k = null, decimal? minimumScore = null)
        {
            string query = $"Observation?{code}={Uri.EscapeDataString(text)}";
            if (k.HasValue)
            {
                query += $"&_vectorK={k.Value}";
            }

            if (minimumScore.HasValue)
            {
                query += $"&_vectorMinScore={minimumScore.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }

            return query;
        }
    }
}
#endif
