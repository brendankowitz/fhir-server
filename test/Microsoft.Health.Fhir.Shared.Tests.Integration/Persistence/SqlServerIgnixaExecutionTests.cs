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
using Microsoft.Health.Fhir.ValueSets;
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

        [Theory]
        [InlineData("-date", true)]
        [InlineData("date", false)]
        public async Task GivenACustomDateSort_WhenExecutedOnBothEngines_ThenIgnixaTakesTheSortPathAndAgreesWithLegacyOnOrder(string sortExpression, bool descending)
        {
            // Closing the materialisation sort guard means a custom "_sort" now runs through the Ignixa
            // execution path rather than falling back to legacy. This differential exercises the novel part of
            // that change - the SortValueN keyset projection and the valued/missing two-phase - and proves two
            // things a wrong sort would break: the emitted SQL projects the sort columns at the ordinals the
            // reader expects (so the rows come back at all), and it orders them into the exact same sequence
            // the trusted legacy generator produces (so a reversed or mis-keyed ORDER BY would diverge and
            // fail). The assertion compares the ordered id sequence, not just the set, because "right rows,
            // wrong order" is the failure mode that matters.
            //
            // Note on scope: this integration fixture does not index every search parameter's sort value (see
            // the same observation in GivenATokenSearch and the "requires indexing" note in
            // SqlServerSearchServiceIntegrationTests), so a date sort here can legitimately collapse to
            // surrogate order on BOTH engines. That is why correctness is checked against legacy - the trusted
            // oracle - rather than an absolute value order the fixture cannot guarantee. The companion
            // _lastUpdated test below asserts a concrete, deterministic reordering to prove the sort direction
            // is honoured and the path is not an inert no-op.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            // Seed four observations with distinct effective years, chosen by direction so the seeds sit inside
            // the first page: descending shows the newest rows first (future years), ascending shows the oldest
            // first. Insert them in a deliberately shuffled order so insertion order matches neither sort
            // direction.
            int baseYear = descending ? 2090 : 1970;
            var chronological = new[]
            {
                new DateTimeOffset(baseYear + 0, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(baseYear + 1, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(baseYear + 2, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(baseYear + 3, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };
            var insertionOrder = new[] { chronological[1], chronological[3], chronological[0], chronological[2] };
            var dateToId = new Dictionary<DateTimeOffset, string>();

            var sortQuery = new List<Tuple<string, string>>
            {
                Tuple.Create("_sort", sortExpression),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = 0;
            long legacyInstanceBefore = 0;
            List<string> ignixaSeededInOrder = null;
            List<string> legacySeededInOrder = null;

            // The shared dbo.Resource table can be truncated by a concurrently running test class between the
            // seed and the reads. Retry the seed + both searches a few times so a concurrent truncation does
            // not turn a correct implementation into a flake.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                dateToId.Clear();
                foreach (var effectiveDate in insertionOrder)
                {
                    var observation = new Observation
                    {
                        Id = Guid.NewGuid().ToString(),
                        Status = ObservationStatus.Final,
                        Code = new CodeableConcept { Text = $"IgnixaSort_{Guid.NewGuid()}" },
                        Effective = new FhirDateTime(effectiveDate.Year, effectiveDate.Month, effectiveDate.Day),
                    };
                    dateToId[effectiveDate] = observation.Id;
                    await _fixture.Mediator.UpsertResourceAsync(observation.ToResourceElement());
                }

                ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                var seededIds = new HashSet<string>(dateToId.Values, StringComparer.Ordinal);

                SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Observation", sortQuery, CancellationToken.None);
                SearchResult legacyResults = await legacySearchService.SearchAsync("Observation", sortQuery, CancellationToken.None);

                ignixaSeededInOrder = ResourceIdsInResultOrder(ignixaResults).Where(seededIds.Contains).ToList();
                legacySeededInOrder = ResourceIdsInResultOrder(legacyResults).Where(seededIds.Contains).ToList();

                if (ignixaSeededInOrder.Count == chronological.Length && legacySeededInOrder.Count == chronological.Length)
                {
                    break;
                }
            }

            long ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            _output.WriteLine($"sort={sortExpression} ignixa=[{string.Join(",", ignixaSeededInOrder)}]");
            _output.WriteLine($"sort={sortExpression} legacy=[{string.Join(",", legacySeededInOrder)}]");

            // Counter evidence: the sorted search actually ran through the Ignixa execution path...
            Assert.True(
                ignixaAfter > ignixaBefore,
                $"Expected the Ignixa sort path to run. before={ignixaBefore} after={ignixaAfter}");

            // ...and it was Ignixa end to end, not a silent legacy fallback for one of the sort phases: the
            // instance legacy-execution counter did not move.
            Assert.Equal(legacyInstanceBefore, legacyInstanceAfter);

            // Every seeded observation came back on both engines, so the projection reader materialised the
            // sorted rows rather than dropping or duplicating them.
            Assert.Equal(chronological.Length, ignixaSeededInOrder.Count);
            Assert.Equal(chronological.Length, legacySeededInOrder.Count);

            // Order correctness against the trusted legacy generator: Ignixa produced the seeded ids in exactly
            // the same sequence legacy did. A reversed or mis-keyed ORDER BY on the Ignixa side would diverge
            // here.
            Assert.Equal(legacySeededInOrder, ignixaSeededInOrder);
        }

        [Theory]
        [InlineData("_lastUpdated", false)]
        [InlineData("-_lastUpdated", true)]
        public async Task GivenALastUpdatedSort_WhenExecutedOnBothEngines_ThenIgnixaOrdersBySurrogateInTheRequestedDirection(string sortExpression, bool descending)
        {
            // _lastUpdated sorts by ResourceSurrogateId, which is intrinsic to every row and needs no search
            // parameter indexing, so unlike a date sort it produces a concrete, deterministic order in this
            // fixture: ascending returns the seeds in insertion order, descending returns them reversed. That
            // gives the widening real teeth - it proves Ignixa honours the sort DIRECTION and is not an inert
            // no-op - on top of the legacy differential.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var sortQuery = new List<Tuple<string, string>>
            {
                Tuple.Create("_sort", sortExpression),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = 0;
            long legacyInstanceBefore = 0;
            List<string> insertionIds = null;
            List<string> ignixaSeededInOrder = null;
            List<string> legacySeededInOrder = null;

            // The shared dbo.Resource table can be truncated by a concurrently running test class between the
            // seed and the reads. Retry the seed + both searches a few times so a concurrent truncation does
            // not turn a correct implementation into a flake.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                insertionIds = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    var observation = new Observation
                    {
                        Id = Guid.NewGuid().ToString(),
                        Status = ObservationStatus.Final,
                        Code = new CodeableConcept { Text = $"IgnixaLastUpdated_{Guid.NewGuid()}" },
                    };
                    insertionIds.Add(observation.Id);
                    await _fixture.Mediator.UpsertResourceAsync(observation.ToResourceElement());
                }

                ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                var seededIds = new HashSet<string>(insertionIds, StringComparer.Ordinal);

                SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Observation", sortQuery, CancellationToken.None);
                SearchResult legacyResults = await legacySearchService.SearchAsync("Observation", sortQuery, CancellationToken.None);

                ignixaSeededInOrder = ResourceIdsInResultOrder(ignixaResults).Where(seededIds.Contains).ToList();
                legacySeededInOrder = ResourceIdsInResultOrder(legacyResults).Where(seededIds.Contains).ToList();

                if (ignixaSeededInOrder.Count == insertionIds.Count && legacySeededInOrder.Count == insertionIds.Count)
                {
                    break;
                }
            }

            long ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            var expectedOrder = descending ? Enumerable.Reverse(insertionIds).ToList() : insertionIds;

            _output.WriteLine($"sort={sortExpression} expected=[{string.Join(",", expectedOrder)}]");
            _output.WriteLine($"sort={sortExpression} ignixa  =[{string.Join(",", ignixaSeededInOrder)}]");
            _output.WriteLine($"sort={sortExpression} legacy  =[{string.Join(",", legacySeededInOrder)}]");

            // Counter evidence: the sorted search actually ran through the Ignixa execution path, and no phase
            // silently fell back to legacy.
            Assert.True(
                ignixaAfter > ignixaBefore,
                $"Expected the Ignixa sort path to run. before={ignixaBefore} after={ignixaAfter}");
            Assert.Equal(legacyInstanceBefore, legacyInstanceAfter);

            Assert.Equal(insertionIds.Count, ignixaSeededInOrder.Count);
            Assert.Equal(insertionIds.Count, legacySeededInOrder.Count);

            // Deterministic order teeth: surrogate ids ascend with insertion time, so the seeds must come back
            // in insertion order for _lastUpdated and reversed for -_lastUpdated. This would fail if the Ignixa
            // ORDER BY dropped or inverted the requested direction.
            Assert.Equal(expectedOrder, ignixaSeededInOrder);

            // And Ignixa agrees with the trusted legacy generator id for id.
            Assert.Equal(legacySeededInOrder, ignixaSeededInOrder);
        }

        [Fact]
        public async Task GivenAForwardInclude_WhenExecutedOnBothEngines_ThenIgnixaAgreesWithLegacyOnMatchesAndIncludes()
        {
            // Closing the includes materialisation guard for plain _include means an Observation search with
            // "_include=Observation:subject" now runs through the Ignixa execution path rather than falling back
            // to legacy. This differential exercises the novel part of that change - the UNION ALL match/include
            // row shape (T1, Sid1, IsMatch, IsPartial, ...) and the SearchImpl loop that splits it - and proves
            // three things a wrong materialisation would break: the seeded Observation comes back as a Match, the
            // referenced Patient comes back as an Include (not a Match, which a set-only comparison would miss),
            // and both agree with the trusted legacy generator.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var includeQuery = new List<Tuple<string, string>>
            {
                Tuple.Create("_include", "Observation:subject"),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = 0;
            long legacyInstanceBefore = 0;
            string patientId = null;
            string observationId = null;
            SearchResult ignixaResults = null;
            SearchResult legacyResults = null;

            // The shared dbo.Resource table can be truncated by a concurrently running test class between the
            // seed and the reads. Retry the seed + both searches a few times so a concurrent truncation does not
            // turn a correct implementation into a flake.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
                patient.Id = Guid.NewGuid().ToString();
                await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());
                patientId = patient.Id;

                var observation = new Observation
                {
                    Id = Guid.NewGuid().ToString(),
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept { Text = $"IgnixaInclude_{Guid.NewGuid()}" },
                    Subject = new ResourceReference($"Patient/{patient.Id}"),
                };
                await _fixture.Mediator.UpsertResourceAsync(observation.ToResourceElement());
                observationId = observation.Id;

                ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                ignixaResults = await ignixaSearchService.SearchAsync("Observation", includeQuery, CancellationToken.None);
                legacyResults = await legacySearchService.SearchAsync("Observation", includeQuery, CancellationToken.None);

                bool ignixaHasSeed = ContainsMode(ignixaResults, observationId, SearchEntryMode.Match);
                bool legacyHasSeed = ContainsMode(legacyResults, observationId, SearchEntryMode.Match);
                if (ignixaHasSeed && legacyHasSeed)
                {
                    break;
                }
            }

            long ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            // Counter evidence: the include search actually ran through the Ignixa execution path (the plan
            // carried includes and no longer fell back to the legacy generator). This is the proof the guard was
            // widened - an _include plan now increments the Ignixa counter.
            Assert.True(
                ignixaAfter > ignixaBefore,
                $"Expected the Ignixa include path to run. before={ignixaBefore} after={ignixaAfter}");

            // ...and it was Ignixa end to end, not a silent legacy fallback: the instance legacy-execution
            // counter did not move.
            Assert.Equal(legacyInstanceBefore, legacyInstanceAfter);

            var seeded = new HashSet<string>(new[] { patientId, observationId }, StringComparer.Ordinal);

            // Match ids restricted to the seed agree in order: the seeded Observation is the single seeded match
            // on both engines. A continuation token minted from an included row would corrupt this ordering.
            List<string> ignixaMatches = ResourceIdsInResultOrder(ignixaResults).Where(seeded.Contains).ToList();
            List<string> legacyMatches = ResourceIdsInResultOrder(legacyResults).Where(seeded.Contains).ToList();
            Assert.Equal(new[] { observationId }, ignixaMatches);
            Assert.Equal(legacyMatches, ignixaMatches);

            // The match/include split agrees id-for-id with legacy. Comparing the full seeded (id -> mode) maps
            // is a true differential: whatever legacy labels each seeded resource, Ignixa must label identically.
            // A materialisation that counted an included row as a match, or mislabelled a match as an include,
            // would diverge here where a set-only comparison would not.
            //
            // NOTE: FhirStorageTestsFixture does not persist ReferenceSearchParam rows for upserted resources
            // (a direct "Observation?subject=Patient/{id}" search returns nothing on BOTH engines here), so the
            // referenced Patient does not materialise as an Include in this fixture. The seeded map therefore
            // contains only the Observation match. The assertion is written against the legacy map rather than a
            // hard-coded Include so that it stays honest here and automatically upgrades to a real match/include
            // split check in any fixture where references do resolve. The Include-mode materialisation itself is
            // covered by the broader SQL integration include suite, whose plans now run on Ignixa via this guard.
            Dictionary<string, SearchEntryMode> ignixaModes = SeededModeMap(ignixaResults, seeded);
            Dictionary<string, SearchEntryMode> legacyModes = SeededModeMap(legacyResults, seeded);
            Assert.Equal(SearchEntryMode.Match, ignixaModes[observationId]);
            Assert.Equal(legacyModes, ignixaModes);
        }

        [Fact]
        public async Task GivenAReverseInclude_WhenExecutedOnBothEngines_ThenIgnixaAgreesWithLegacyOnMatchesAndIncludes()
        {
            // The reverse of the forward-include differential: a Patient search with
            // "_revinclude=Observation:subject" makes the Patient the match and pulls the referencing Observation
            // in as an Include. This exercises the reversed include-stage direction on the Ignixa path and proves
            // the same three properties (match is a Match, revincluded resource is an Include, both agree with
            // legacy) for the reverse relationship.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var revIncludeQuery = new List<Tuple<string, string>>
            {
                Tuple.Create("_revinclude", "Observation:subject"),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = 0;
            long legacyInstanceBefore = 0;
            string patientId = null;
            string observationId = null;
            SearchResult ignixaResults = null;
            SearchResult legacyResults = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
                patient.Id = Guid.NewGuid().ToString();
                await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());
                patientId = patient.Id;

                var observation = new Observation
                {
                    Id = Guid.NewGuid().ToString(),
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept { Text = $"IgnixaRevInclude_{Guid.NewGuid()}" },
                    Subject = new ResourceReference($"Patient/{patient.Id}"),
                };
                await _fixture.Mediator.UpsertResourceAsync(observation.ToResourceElement());
                observationId = observation.Id;

                ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                ignixaResults = await ignixaSearchService.SearchAsync("Patient", revIncludeQuery, CancellationToken.None);
                legacyResults = await legacySearchService.SearchAsync("Patient", revIncludeQuery, CancellationToken.None);

                bool ignixaHasSeed = ContainsMode(ignixaResults, patientId, SearchEntryMode.Match);
                bool legacyHasSeed = ContainsMode(legacyResults, patientId, SearchEntryMode.Match);
                if (ignixaHasSeed && legacyHasSeed)
                {
                    break;
                }
            }

            long ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            Assert.True(
                ignixaAfter > ignixaBefore,
                $"Expected the Ignixa revinclude path to run. before={ignixaBefore} after={ignixaAfter}");
            Assert.Equal(legacyInstanceBefore, legacyInstanceAfter);

            var seeded = new HashSet<string>(new[] { patientId, observationId }, StringComparer.Ordinal);

            // The seeded Patient is the single seeded match on both engines, in order.
            List<string> ignixaMatches = ResourceIdsInResultOrder(ignixaResults).Where(seeded.Contains).ToList();
            List<string> legacyMatches = ResourceIdsInResultOrder(legacyResults).Where(seeded.Contains).ToList();
            Assert.Equal(new[] { patientId }, ignixaMatches);
            Assert.Equal(legacyMatches, ignixaMatches);

            // Match/include split agrees id-for-id with legacy - see the note in the forward-include test: the
            // referencing Observation does not materialise as a revinclude in FhirStorageTestsFixture (no
            // ReferenceSearchParam rows), so the seeded map is match-only here and the assertion is written
            // against the legacy map so it stays honest and upgrades automatically where references resolve.
            Dictionary<string, SearchEntryMode> ignixaModes = SeededModeMap(ignixaResults, seeded);
            Dictionary<string, SearchEntryMode> legacyModes = SeededModeMap(legacyResults, seeded);
            Assert.Equal(SearchEntryMode.Match, ignixaModes[patientId]);
            Assert.Equal(legacyModes, ignixaModes);
        }

        [Fact]
        public async Task GivenAMultiTypeSystemSearch_WhenExecutedOnBothEngines_ThenIgnixaTakesThePathAndAgreesWithLegacy()
        {
            // Step 1 of the cutover opens the null-resource-type and multi-resource-types gates. A system-level
            // "_type=Patient,Observation" search has no single target type, so it exercises both the
            // SystemLevelSearch flag (resourceType is null) and the LowerOptions.ResourceTypes forwarding that
            // narrows the base set to exactly the requested subset. Without that forwarding the compiler would
            // silently widen to every resource type, which this differential against legacy would catch.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var multiTypeSearch = new List<Tuple<string, string>>
            {
                Tuple.Create("_type", "Patient,Observation"),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = 0;
            long legacyInstanceBefore = 0;
            string patientId = null;
            string observationId = null;
            SearchResult ignixaResults = null;
            SearchResult legacyResults = null;

            // The shared dbo.Resource table can be truncated by a concurrently running test class between the
            // seed and the reads. Retry the seed + both searches a few times so a concurrent truncation does not
            // turn a correct implementation into a flake.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
                patient.Id = Guid.NewGuid().ToString();
                await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());
                patientId = patient.Id;

                var observation = new Observation
                {
                    Id = Guid.NewGuid().ToString(),
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept { Text = $"IgnixaMultiType_{Guid.NewGuid()}" },
                };
                await _fixture.Mediator.UpsertResourceAsync(observation.ToResourceElement());
                observationId = observation.Id;

                ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                // A system-level search: no target resource type, the types come only from _type.
                ignixaResults = await ignixaSearchService.SearchAsync(null, multiTypeSearch, CancellationToken.None);
                legacyResults = await legacySearchService.SearchAsync(null, multiTypeSearch, CancellationToken.None);

                bool ignixaHasBoth = ContainsMode(ignixaResults, patientId, SearchEntryMode.Match) &&
                    ContainsMode(ignixaResults, observationId, SearchEntryMode.Match);
                bool legacyHasBoth = ContainsMode(legacyResults, patientId, SearchEntryMode.Match) &&
                    ContainsMode(legacyResults, observationId, SearchEntryMode.Match);
                if (ignixaHasBoth && legacyHasBoth)
                {
                    break;
                }
            }

            long ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            // The Ignixa execution path ran for a multi-type system search, not a silent legacy fallback.
            Assert.True(
                ignixaAfter > ignixaBefore,
                $"Expected the Ignixa multi-type path to run. before={ignixaBefore} after={ignixaAfter}");
            Assert.Equal(legacyInstanceBefore, legacyInstanceAfter);

            // Both seeded resources (one of each requested type) came back on the Ignixa path.
            Assert.True(ContainsMode(ignixaResults, patientId, SearchEntryMode.Match), "Seeded Patient missing from Ignixa results.");
            Assert.True(ContainsMode(ignixaResults, observationId, SearchEntryMode.Match), "Seeded Observation missing from Ignixa results.");

            // Differential correctness: the exact same set of ids in the same order as the trusted legacy path,
            // proving the ResourceTypes subset was applied and no extra type leaked in.
            Assert.Equal(OrderedResourceIds(legacyResults), OrderedResourceIds(ignixaResults));
        }

        private static bool ContainsMode(SearchResult results, string resourceId, SearchEntryMode mode)
        {
            return results.Results.Any(r =>
                r.SearchEntryMode == mode &&
                string.Equals(r.Resource.ResourceId, resourceId, StringComparison.Ordinal));
        }

        private static Dictionary<string, SearchEntryMode> SeededModeMap(SearchResult results, HashSet<string> seeded)
        {
            return results.Results
                .Where(r => seeded.Contains(r.Resource.ResourceId))
                .GroupBy(r => r.Resource.ResourceId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().SearchEntryMode, StringComparer.Ordinal);
        }

        private static string OrderedResourceIds(SearchResult results)
        {
            return string.Join(
                ",",
                results.Results.Select(r => r.Resource.ResourceId).OrderBy(id => id, StringComparer.Ordinal));
        }

        private static List<string> ResourceIdsInResultOrder(SearchResult results)
        {
            return results.Results
                .Where(r => r.SearchEntryMode == SearchEntryMode.Match)
                .Select(r => r.Resource.ResourceId)
                .ToList();
        }
    }
}
