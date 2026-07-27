// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.SqlServer.Features.Search;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Test.Utilities;
using Xunit;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.Integration.Persistence
{
    [FhirStorageTestsFixtureArgumentSets(DataStore.SqlServer)]
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.DataSourceValidation)]
    public class SqlServerIgnixaExecutionTests : IClassFixture<FhirStorageTestsFixture>
    {
        private readonly FhirStorageTestsFixture _fixture;
        private readonly ITestOutputHelper _output;

        public SqlServerIgnixaExecutionTests(FhirStorageTestsFixture fixture, ITestOutputHelper testOutputHelper)
        {
            _fixture = fixture;
            _output = testOutputHelper;
        }

        [Fact]
        public async Task GivenAnEligibleSearch_WhenExecuted_ThenTheIgnixaSqlPathRunsTheQueryAndMaterialisesRows()
        {
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var matchAll = new List<Tuple<string, string>>();

            // The shared dbo.Resource table can be truncated by a concurrently running test class, which would
            // remove the seeded Patient before this search reads it. Retry the seed + search a few times so a
            // concurrent truncation does not turn a correct implementation into a flake.
            long ignixaBefore = 0;
            long legacyInstanceBefore = 0;
            SearchResult ignixaResults = null;
            SearchResult legacyResults = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
                patient.Id = Guid.NewGuid().ToString();
                await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());

                ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                // A match-all query is eligible for the Ignixa execution path, so it exercises the emitted SQL
                // and the projection reader end to end.
                ignixaResults = await ignixaSearchService.SearchAsync("Patient", matchAll, CancellationToken.None);
                legacyResults = await legacySearchService.SearchAsync("Patient", matchAll, CancellationToken.None);

                if (ignixaResults != null && ignixaResults.Results.Any())
                {
                    break;
                }
            }

            long ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            // The dedicated Ignixa search service actually ran the Ignixa-emitted SQL for this query.
            Assert.True(
                ignixaAfter > ignixaBefore,
                $"Expected the Ignixa execution path to run. before={ignixaBefore} after={ignixaAfter}");

            // It ran Ignixa, not a silent legacy fallback: the instance legacy-execution counter did not move.
            Assert.Equal(legacyInstanceBefore, legacyInstanceAfter);

            // The projection reader materialised rows: at least the seeded Patient came back.
            Assert.NotNull(ignixaResults);
            Assert.NotEmpty(ignixaResults.Results);

            // Differential correctness: the Ignixa reader returns exactly the same resources as the trusted
            // legacy generator for the same query, proving the projection column ordinals are wired correctly.
            string ignixaIds = OrderedResourceIds(ignixaResults);
            string legacyIds = OrderedResourceIds(legacyResults);
            Assert.Equal(legacyIds, ignixaIds);
        }

        [Theory]
        [InlineData("gender", "female")]
        [InlineData("_tag", "http://example.org/tag|ignixa-token-probe")]
        public async Task GivenATokenSearch_WhenExecutedOnBothEngines_ThenIgnixaAgreesWithLegacy(string parameterName, string parameterValue)
        {
            // The capability checker defers every user token/composite parameter to legacy, and its doc comment
            // asserts that Ignixa "emits incorrect SQL for token-family search parameters". Token searches are the
            // most common FHIR search kind, so that gate decides whether this cutover is meaningful or nearly
            // inert. This test is the differential that would substantiate the claim: it compares both engines
            // against a real schema, which is the only thing that separates a compiler defect from a
            // symbol-resolution defect on this side.
            //
            // It asserts agreement rather than a non-empty result on purpose. This fixture does not index every
            // search parameter, so a token search can legitimately return nothing on both engines — and requiring
            // rows would turn "the fixture has no data for this parameter" into a failure that looks like an
            // Ignixa defect. Disagreement is the signal; row count is not.
            //
            // What this proves today: while the capability gate defers token parameters, *both* services fall
            // back to legacy for these searches, so the comparison is legacy-versus-legacy and passes trivially.
            // It is a latch rather than a live check — it gains teeth the moment that gate is narrowed, and it is
            // written now so narrowing cannot happen without a differential guarding it. The evidence that the
            // gate is over-broad came from disabling it and running this whole suite, which produced no new SQL
            // failures.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
            patient.Id = Guid.NewGuid().ToString();
            patient.Gender = AdministrativeGender.Female;
            patient.Meta = new Meta
            {
                Tag = new List<Coding> { new Coding("http://example.org/tag", "ignixa-token-probe") },
            };
            await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());

            var tokenSearch = new List<Tuple<string, string>>
            {
                Tuple.Create(parameterName, parameterValue),
            };

            SearchResult legacyResults = await legacySearchService.SearchAsync("Patient", tokenSearch, CancellationToken.None);
            SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Patient", tokenSearch, CancellationToken.None);

            _output.WriteLine($"{parameterName}={parameterValue} -> legacy {legacyResults.Results.Count()} rows, ignixa {ignixaResults.Results.Count()} rows");

            Assert.Equal(OrderedResourceIds(legacyResults), OrderedResourceIds(ignixaResults));
        }

        private static string OrderedResourceIds(SearchResult results)
        {
            return string.Join(
                ",",
                results.Results.Select(r => r.Resource.ResourceId).OrderBy(id => id, StringComparer.Ordinal));
        }
    }
}
