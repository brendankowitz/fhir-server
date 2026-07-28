// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Converters;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer.Features.Search;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Fhir.Tests.Common.Mocks;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
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
        private static readonly SemaphoreSlim _searchIndexerLock = new(1, 1);
        private static ISearchIndexer _searchIndexer;

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

        [Fact]
        public async Task GivenALatestPlusHistorySearch_WhenExecutedOnBothEngines_ThenIgnixaTakesThePathAndReturnsBothVersions()
        {
            // Step 2 of the cutover maps the server's ResourceVersionType onto the compiler's relaxation-only
            // ResourceVisibility. Latest|History must relax the IsHistory=0 filter so BOTH the current version
            // and the prior (now-historical) version come back. Seeding is an upsert of the same id twice, which
            // moves version 1 into history and leaves version 2 as latest. A single-version assertion would not
            // prove the relaxation actually happened, so this walks the exact (id, version) pairs and requires
            // two rows that match the trusted legacy path id-for-id and version-for-version.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            const ResourceVersionType latestPlusHistory = ResourceVersionType.Latest | ResourceVersionType.History;

            long ignixaBefore = 0;
            long legacyInstanceBefore = 0;
            string patientId = null;
            SearchResult ignixaResults = null;
            SearchResult legacyResults = null;

            // The shared dbo.Resource table can be truncated by a concurrently running test class between the
            // seed and the reads. Retry the seed + both searches a few times so a concurrent truncation does not
            // turn a correct implementation into a flake.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
                patient.Id = Guid.NewGuid().ToString();
                patientId = patient.Id;

                // First upsert = version 1. Second upsert of the same id = version 2, which moves version 1 into
                // history (IsHistory = 1) and leaves version 2 as the latest.
                await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());
                patient.Gender = patient.Gender == AdministrativeGender.Female
                    ? AdministrativeGender.Male
                    : AdministrativeGender.Female;
                await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());

                var versionSearch = new List<Tuple<string, string>>
                {
                    Tuple.Create("_count", "1000"),
                };

                ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                ignixaResults = await ignixaSearchService.SearchAsync(
                    "Patient", versionSearch, CancellationToken.None, resourceVersionTypes: latestPlusHistory);
                legacyResults = await legacySearchService.SearchAsync(
                    "Patient", versionSearch, CancellationToken.None, resourceVersionTypes: latestPlusHistory);

                if (ignixaResults.Results.Any(r => string.Equals(r.Resource.ResourceId, patientId, StringComparison.Ordinal)) &&
                    legacyResults.Results.Any(r => string.Equals(r.Resource.ResourceId, patientId, StringComparison.Ordinal)))
                {
                    break;
                }
            }

            long ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            // The Ignixa execution path ran for a Latest|History search, not a silent legacy fallback.
            Assert.True(
                ignixaAfter > ignixaBefore,
                $"Expected the Ignixa Latest|History path to run. before={ignixaBefore} after={ignixaAfter}");
            Assert.Equal(legacyInstanceBefore, legacyInstanceAfter);

            // Differential correctness under the relaxed visibility: whatever set of versions the trusted legacy
            // path surfaces for this id, the Ignixa path must surface exactly the same (id, version) set. If the
            // visibility mapping over- or under-relaxed (e.g. leaked a historical version legacy hides, or hid one
            // legacy shows), this would diverge.
            List<string> ignixaVersions = OrderedResourceIdVersions(ignixaResults, patientId);
            List<string> legacyVersions = OrderedResourceIdVersions(legacyResults, patientId);
            Assert.NotEmpty(legacyVersions);
            Assert.Equal(legacyVersions, ignixaVersions);
        }

        [Fact]
        public async Task GivenAHistoryOnlySearch_WhenExecutedOnBothEngines_ThenIgnixaReturnsOnlySupersededVersionsLikeLegacy()
        {
            // History alone is not a relaxation - legacy renders it as an exact IsHistory = 1 filter, so the
            // CURRENT version must be excluded. That asymmetry is the whole point of the tri-state visibility
            // model: a mapping that merely relaxed IsHistory = 0 would return both versions and pass a test that
            // only checked "the historical version is present".
            //
            // Observation (not Patient) is used deliberately: the fixture's mocked capability statement marks
            // Observation as Versioned, so upserting twice retains version 1 as history. Patient carries the
            // default policy and its prior version is simply overwritten, which would leave nothing to find and
            // make this test vacuous.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var observation = new Observation
            {
                Id = Guid.NewGuid().ToString(),
                Status = ObservationStatus.Preliminary,
                Code = new CodeableConcept { Text = $"IgnixaHistory_{Guid.NewGuid()}" },
            };
            string observationId = observation.Id;

            await _fixture.Mediator.UpsertResourceAsync(observation.ToResourceElement());
            observation.Status = ObservationStatus.Final;
            await _fixture.Mediator.UpsertResourceAsync(observation.ToResourceElement());

            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", observationId),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync(
                "Observation", query, CancellationToken.None, resourceVersionTypes: ResourceVersionType.History);
            SearchResult legacyResults = await legacySearchService.SearchAsync(
                "Observation", query, CancellationToken.None, resourceVersionTypes: ResourceVersionType.History);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                "Expected the history-only search to run on Ignixa.");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            List<string> legacyVersions = OrderedResourceIdVersions(legacyResults, observationId);
            List<string> ignixaVersions = OrderedResourceIdVersions(ignixaResults, observationId);

            // Anti-vacuity, both directions: legacy found the superseded version 1, and did NOT surface the
            // current version 2. Without the second assertion a relaxation-only mapping would still pass.
            Assert.Equal(new[] { $"{observationId}/1" }, legacyVersions);
            Assert.Equal(legacyVersions, ignixaVersions);
        }

        [Fact]
        public async Task GivenASoftDeletedOnlySearch_WhenExecutedOnBothEngines_ThenIgnixaReturnsOnlyDeletedRowsLikeLegacy()
        {
            // SoftDeleted alone renders in legacy as an exact IsDeleted = 1 filter with NO history filter. Two
            // resources are seeded and only one is deleted, so the live one must be absent from both engines -
            // which is what distinguishes an exact filter from a relaxed one.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var deletedPatient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
            deletedPatient.Id = Guid.NewGuid().ToString();
            await _fixture.Mediator.UpsertResourceAsync(deletedPatient.ToResourceElement());
            await _fixture.Mediator.DeleteResourceAsync(
                new ResourceKey("Patient", deletedPatient.Id), Core.Messages.Delete.DeleteOperation.SoftDelete);

            var livePatient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
            livePatient.Id = Guid.NewGuid().ToString();
            await _fixture.Mediator.UpsertResourceAsync(livePatient.ToResourceElement());

            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", $"{deletedPatient.Id},{livePatient.Id}"),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync(
                "Patient", query, CancellationToken.None, resourceVersionTypes: ResourceVersionType.SoftDeleted);
            SearchResult legacyResults = await legacySearchService.SearchAsync(
                "Patient", query, CancellationToken.None, resourceVersionTypes: ResourceVersionType.SoftDeleted);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                "Expected the soft-deleted-only search to run on Ignixa.");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            var seeded = new HashSet<string>(new[] { deletedPatient.Id, livePatient.Id }, StringComparer.Ordinal);
            List<string> legacyMatches = ResourceIdsInResultOrder(legacyResults).Where(seeded.Contains).ToList();
            List<string> ignixaMatches = ResourceIdsInResultOrder(ignixaResults).Where(seeded.Contains).ToList();

            Assert.Equal(new[] { deletedPatient.Id }, legacyMatches);
            Assert.Equal(legacyMatches, ignixaMatches);
        }

        [Fact]
        public async Task GivenAnIgnoreSearchParamHashSearch_WhenExecutedOnBothEngines_ThenIgnixaTakesThePathAndAgreesWithLegacy()
        {
            // Step 3 removes the ignore-search-param-hash gate. The flag is consumed only by the reindex-only
            // SearchForReindexInternalAsync entry point, never by the main search path that invokes this router,
            // so on the router path it is inert and both engines must return identical rows. Passing the flag on
            // a normal Patient search exercises exactly that: the request reaches the router and is now eligible.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var hashSearch = new List<Tuple<string, string>>
            {
                Tuple.Create(Core.Features.KnownQueryParameterNames.IgnoreSearchParamHash, "true"),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = 0;
            long legacyInstanceBefore = 0;
            string patientId = null;
            SearchResult ignixaResults = null;
            SearchResult legacyResults = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
                patient.Id = Guid.NewGuid().ToString();
                await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());
                patientId = patient.Id;

                ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                ignixaResults = await ignixaSearchService.SearchAsync("Patient", hashSearch, CancellationToken.None);
                legacyResults = await legacySearchService.SearchAsync("Patient", hashSearch, CancellationToken.None);

                if (ignixaResults.Results.Any(r => string.Equals(r.Resource.ResourceId, patientId, StringComparison.Ordinal)) &&
                    legacyResults.Results.Any(r => string.Equals(r.Resource.ResourceId, patientId, StringComparison.Ordinal)))
                {
                    break;
                }
            }

            long ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            // The Ignixa execution path ran for an ignore-search-param-hash search, not a silent legacy fallback.
            Assert.True(
                ignixaAfter > ignixaBefore,
                $"Expected the Ignixa ignore-search-param-hash path to run. before={ignixaBefore} after={ignixaAfter}");
            Assert.Equal(legacyInstanceBefore, legacyInstanceAfter);

            // The inert flag left the row set unchanged: same ids in the same order as the trusted legacy path.
            Assert.Equal(OrderedResourceIds(legacyResults), OrderedResourceIds(ignixaResults));
        }

        [Fact]
        public async Task GivenAMatchAllSearch_WhenPagedAcrossBoundaries_ThenIgnixaKeysetPagingAgreesWithLegacy()
        {
            // Step 4 wires the continuation token into the Ignixa plan as a keyset PageSpec. Without it the Ignixa
            // plan would ignore the token and re-return page one - and a single-page assertion could never see
            // that. This walks several small pages on both engines and compares the *concatenated* match
            // sequence, which is the only shape that catches a repeated page (a duplicate id) or a skipped page
            // (a missing id).
            //
            // Ascending surrogate-id order makes the *front* of a match-all result stable under concurrent
            // inserts by other test classes: new rows always take higher surrogate ids and append at the end, so
            // the first few pages do not move. Seeding more rows than the walk reads keeps the walk inside that
            // stable prefix. Concurrent truncation wipes the prefix instead; that is the one hazard, so the whole
            // seed + walk + compare is retried, exactly like the other tests in this class.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            const int pageSize = 2;
            const int maxPages = 4;

            long ignixaBefore = 0;
            long legacyInstanceBefore = 0;
            long ignixaAfter = 0;
            long legacyInstanceAfter = 0;
            List<string> ignixaWalk = null;
            List<string> legacyWalk = null;
            int ignixaPages = 0;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                // Seed more patients than the walk reads (pageSize * maxPages = 8) so the walk never reaches the
                // end of the table and stays entirely inside the concurrency-stable prefix.
                for (int i = 0; i < 10; i++)
                {
                    var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
                    patient.Id = Guid.NewGuid().ToString();
                    await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());
                }

                ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                (ignixaWalk, ignixaPages) = await WalkMatchAllPagesAsync(ignixaSearchService, pageSize, maxPages, CancellationToken.None);

                ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                (legacyWalk, _) = await WalkMatchAllPagesAsync(legacySearchService, pageSize, maxPages, CancellationToken.None);

                // A clean run walked at least two pages and both engines returned the same front sequence. If a
                // concurrent truncation disturbed the prefix between the two walks, retry.
                if (ignixaPages >= 2 && ignixaWalk.SequenceEqual(legacyWalk, StringComparer.Ordinal))
                {
                    break;
                }
            }

            _output.WriteLine($"ignixaPages={ignixaPages} ignixa=[{string.Join(",", ignixaWalk)}]");
            _output.WriteLine($"legacy=[{string.Join(",", legacyWalk)}]");

            // Multi-page: the walk actually crossed at least one page boundary, so the continuation token was
            // exercised rather than a single page trivially matching.
            Assert.True(ignixaPages >= 2, $"Expected the Ignixa walk to span at least two pages, got {ignixaPages}.");

            // Counter evidence: every Ignixa page ran the Ignixa execution path - the first page (no token) and
            // each subsequent page (surrogate-keyset continuation token). A silent legacy fallback on any page
            // would leave the Ignixa counter short of the page count and move the legacy counter instead.
            Assert.Equal(ignixaPages, ignixaAfter - ignixaBefore);
            Assert.Equal(legacyInstanceBefore, legacyInstanceAfter);

            // No duplicate: no id appears twice across the paged walk, so no page was repeated - the exact
            // failure a token that resets to page one would produce.
            Assert.Equal(ignixaWalk.Count, ignixaWalk.Distinct(StringComparer.Ordinal).Count());

            // No gap and correct order: the concatenated Ignixa page sequence equals the trusted legacy walk over
            // the same window, so nothing was skipped and the keyset boundary landed exactly where legacy's did.
            Assert.Equal(legacyWalk, ignixaWalk);
        }

        private static async Task<(List<string> Ids, int Pages)> WalkMatchAllPagesAsync(
            ISearchService service,
            int pageSize,
            int maxPages,
            CancellationToken cancellationToken)
        {
            var ids = new List<string>();
            string continuation = null;
            int pages = 0;

            while (pages < maxPages)
            {
                var query = new List<Tuple<string, string>>
                {
                    Tuple.Create("_count", pageSize.ToString(CultureInfo.InvariantCulture)),
                };

                if (continuation != null)
                {
                    // Feed the previous page's token back the way the REST layer does: re-encoded under the "ct"
                    // query parameter, which SearchOptionsFactory decodes back into SqlSearchOptions.ContinuationToken.
                    query.Add(Tuple.Create(
                        Core.Features.KnownQueryParameterNames.ContinuationToken,
                        ContinuationTokenEncoder.Encode(continuation)));
                }

                SearchResult page = await service.SearchAsync("Patient", query, cancellationToken);
                pages++;
                ids.AddRange(ResourceIdsInResultOrder(page));

                continuation = page.ContinuationToken;
                if (string.IsNullOrEmpty(continuation))
                {
                    break;
                }
            }

            return (ids, pages);
        }

        [Fact]
        public async Task GivenClinicalScopes_WhenExecutedOnBothEngines_ThenIgnixaEnforcesThemLikeLegacy()
        {
            // The proof that opening the access-control gate is safe. Two searches differing only in which
            // resource type the caller's SMART scope grants: the same query must return the seeded Patient when
            // the scope allows Patient and nothing when it does not. Both cases run through Ignixa (asserted via
            // the counters), so the allowed case proves the plan is not simply blocking everything, and the denied
            // case proves the allow-list actually restricts. Either assertion alone would pass against a broken
            // implementation - a plan that returned everything passes the first, one that returned nothing passes
            // the second - so only the pair is evidence.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
            patient.Id = Guid.NewGuid().ToString();
            await _fixture.Mediator.UpsertResourceAsync(patient.ToResourceElement());

            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", patient.Id),
            };

            // A fresh request context rather than mutating the fixture's: stubbing AccessControlContext through the
            // accessor chain does not take (NSubstitute binds the Returns to get_RequestContext), and the fixture is
            // shared across the class, so the original is restored in the finally below.
            RequestContextAccessor<IFhirRequestContext> contextAccessor = fixture.FhirRequestContextAccessor;
            IFhirRequestContext originalContext = contextAccessor.RequestContext;

            var accessControl = new AccessControlContext { ApplyFineGrainedAccessControl = true };
            var scopedContext = Substitute.For<IFhirRequestContext>();
            scopedContext.AccessControlContext.Returns(accessControl);
            scopedContext.CorrelationId.Returns(Guid.NewGuid().ToString());
            scopedContext.RouteName.Returns("routeName");
            scopedContext.RequestHeaders.Returns(new Dictionary<string, StringValues>());
            scopedContext.ResponseHeaders.Returns(new Dictionary<string, StringValues>());
            contextAccessor.RequestContext.Returns(scopedContext);

            try
            {
                // Scope grants Patient: both engines return the seeded resource.
                accessControl.AllowedResourceActions.Add(new ScopeRestriction(KnownResourceTypes.Patient, DataActions.Read, "user"));

                long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                SearchResult allowedIgnixa = await ignixaSearchService.SearchAsync("Patient", query, CancellationToken.None);
                SearchResult allowedLegacy = await legacySearchService.SearchAsync("Patient", query, CancellationToken.None);

                // Counter evidence: the scope-restricted search really did run through Ignixa rather than falling
                // back. Without this the rest of the test would pass just as happily against the closed gate.
                Assert.True(
                    ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                    $"Expected the scoped search to run on Ignixa. before={ignixaBefore} after={ignixaSearchService.InstanceIgnixaExecutedQueryCount}");
                Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

                Assert.Equal(new[] { patient.Id }, ResourceIdsInResultOrder(allowedIgnixa));
                Assert.Equal(ResourceIdsInResultOrder(allowedLegacy), ResourceIdsInResultOrder(allowedIgnixa));

                // Scope grants only Observation: the very same Patient search must now return nothing, because the
                // match set is intersected with the allowed types. Legacy reaches the same answer by a different
                // route (no scope matches the searched type, so it emits a blocking ResourceType = "none"
                // predicate), which is exactly what makes this a differential rather than a self-consistent check.
                accessControl.AllowedResourceActions.Clear();
                accessControl.AllowedResourceActions.Add(new ScopeRestriction(KnownResourceTypes.Observation, DataActions.Read, "user"));

                long deniedIgnixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
                long deniedLegacyBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

                SearchResult deniedIgnixa = await ignixaSearchService.SearchAsync("Patient", query, CancellationToken.None);
                SearchResult deniedLegacy = await legacySearchService.SearchAsync("Patient", query, CancellationToken.None);

                Assert.True(
                    ignixaSearchService.InstanceIgnixaExecutedQueryCount > deniedIgnixaBefore,
                    "Expected the denied search to also run on Ignixa; a fallback would prove nothing about the allow-list.");
                Assert.Equal(deniedLegacyBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

                Assert.Empty(ResourceIdsInResultOrder(deniedIgnixa));
                Assert.Empty(ResourceIdsInResultOrder(deniedLegacy));
            }
            finally
            {
                contextAccessor.RequestContext.Returns(originalContext);
            }
        }

        [Fact]
        public async Task GivenAForwardIncludeOverIndexedReferences_WhenExecutedOnBothEngines_ThenIgnixaMaterialisesTheSameIncludedRowsAsLegacy()
        {
            // The include differential that is NOT vacuous. Unlike the mediator-seeded include tests in this file,
            // this one writes real dbo.ReferenceSearchParam rows (see UpsertWithSearchIndicesAsync), so the
            // referenced Patient genuinely materialises as an Include entry. Deleting the Ignixa include emitter
            // would fail this test, which is the property the mediator-seeded variants cannot claim.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
            patient.Id = Guid.NewGuid().ToString();
            string patientId = await UpsertWithSearchIndicesAsync(patient);

            var observation = new Observation
            {
                Id = Guid.NewGuid().ToString(),
                Status = ObservationStatus.Final,
                Code = new CodeableConcept { Text = $"IgnixaIndexedInclude_{Guid.NewGuid()}" },
                Subject = new ResourceReference($"Patient/{patientId}"),
            };
            string observationId = await UpsertWithSearchIndicesAsync(observation);

            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", observationId),
                Tuple.Create("_include", "Observation:subject"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Observation", query, CancellationToken.None);
            SearchResult legacyResults = await legacySearchService.SearchAsync("Observation", query, CancellationToken.None);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                "Expected the include search to run on Ignixa.");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            // The reference index really did get written - without this guard the rest of the test would pass
            // vacuously again if seeding silently regressed, which is exactly the failure mode being fixed here.
            Assert.True(
                ContainsMode(legacyResults, patientId, SearchEntryMode.Include),
                "Legacy did not return the referenced Patient as an Include; the reference index was not seeded, so this test would be vacuous.");

            Assert.True(ContainsMode(ignixaResults, observationId, SearchEntryMode.Match));
            Assert.True(ContainsMode(ignixaResults, patientId, SearchEntryMode.Include));

            var seeded = new HashSet<string>(new[] { patientId, observationId }, StringComparer.Ordinal);
            Assert.Equal(SeededModeMap(legacyResults, seeded), SeededModeMap(ignixaResults, seeded));
        }

        [Fact]
        public async Task GivenAReverseIncludeOverIndexedReferences_WhenExecutedOnBothEngines_ThenIgnixaMaterialisesTheSameIncludedRowsAsLegacy()
        {
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
            patient.Id = Guid.NewGuid().ToString();
            string patientId = await UpsertWithSearchIndicesAsync(patient);

            var observation = new Observation
            {
                Id = Guid.NewGuid().ToString(),
                Status = ObservationStatus.Final,
                Code = new CodeableConcept { Text = $"IgnixaIndexedRevInclude_{Guid.NewGuid()}" },
                Subject = new ResourceReference($"Patient/{patientId}"),
            };
            string observationId = await UpsertWithSearchIndicesAsync(observation);

            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", patientId),
                Tuple.Create("_revinclude", "Observation:subject"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Patient", query, CancellationToken.None);
            SearchResult legacyResults = await legacySearchService.SearchAsync("Patient", query, CancellationToken.None);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                "Expected the revinclude search to run on Ignixa.");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            Assert.True(
                ContainsMode(legacyResults, observationId, SearchEntryMode.Include),
                "Legacy did not return the referencing Observation as an Include; the reference index was not seeded, so this test would be vacuous.");

            Assert.True(ContainsMode(ignixaResults, patientId, SearchEntryMode.Match));
            Assert.True(ContainsMode(ignixaResults, observationId, SearchEntryMode.Include));

            var seeded = new HashSet<string>(new[] { patientId, observationId }, StringComparer.Ordinal);
            Assert.Equal(SeededModeMap(legacyResults, seeded), SeededModeMap(ignixaResults, seeded));
        }

        [Fact]
        public async Task GivenAWildcardInclude_WhenExecutedOnBothEngines_ThenIgnixaMaterialisesTheSameIncludedRowsAsLegacy()
        {
            // "_include=*" lowers to a reference-parameter-less join in Ignixa. The row set that produces had never
            // been compared against legacy, which is why the router refused it; this is that comparison.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var organization = new Organization { Id = Guid.NewGuid().ToString(), Name = $"IgnixaWildcard_{Guid.NewGuid()}" };
            string organizationId = await UpsertWithSearchIndicesAsync(organization);

            var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
            patient.Id = Guid.NewGuid().ToString();
            patient.ManagingOrganization = new ResourceReference($"Organization/{organizationId}");
            string patientId = await UpsertWithSearchIndicesAsync(patient);

            var observation = new Observation
            {
                Id = Guid.NewGuid().ToString(),
                Status = ObservationStatus.Final,
                Code = new CodeableConcept { Text = $"IgnixaWildcardInclude_{Guid.NewGuid()}" },
                Subject = new ResourceReference($"Patient/{patientId}"),
            };
            string observationId = await UpsertWithSearchIndicesAsync(observation);

            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", observationId),
                Tuple.Create("_include", "*"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Observation", query, CancellationToken.None);
            SearchResult legacyResults = await legacySearchService.SearchAsync("Observation", query, CancellationToken.None);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                "Expected the wildcard include search to run on Ignixa.");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            Assert.True(
                ContainsMode(legacyResults, patientId, SearchEntryMode.Include),
                "Legacy did not return the referenced Patient as an Include; the reference index was not seeded, so this test would be vacuous.");

            var seeded = new HashSet<string>(new[] { organizationId, patientId, observationId }, StringComparer.Ordinal);
            Assert.Equal(SeededModeMap(legacyResults, seeded), SeededModeMap(ignixaResults, seeded));
        }

        [Fact]
        public async Task GivenAnIterateInclude_WhenExecutedOnBothEngines_ThenIgnixaMaterialisesTheSameClosureAsLegacy()
        {
            // ":iterate" is where Ignixa and legacy differ structurally: legacy runs a fixed-point iteration,
            // Ignixa resolves the closure in a single topological pass in Lower. The two only agree if the closure
            // they compute is the same, so the seed is a genuine two-hop chain
            // (Observation -> Patient -> Organization) and the assertion is on the full seeded mode map: a missing
            // second hop, or an Organization mislabelled as a match, both fail here.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var organization = new Organization { Id = Guid.NewGuid().ToString(), Name = $"IgnixaIterate_{Guid.NewGuid()}" };
            string organizationId = await UpsertWithSearchIndicesAsync(organization);

            var patient = (Patient)Samples.GetJsonSample("Patient").ToPoco();
            patient.Id = Guid.NewGuid().ToString();
            patient.ManagingOrganization = new ResourceReference($"Organization/{organizationId}");
            string patientId = await UpsertWithSearchIndicesAsync(patient);

            var observation = new Observation
            {
                Id = Guid.NewGuid().ToString(),
                Status = ObservationStatus.Final,
                Code = new CodeableConcept { Text = $"IgnixaIterateInclude_{Guid.NewGuid()}" },
                Subject = new ResourceReference($"Patient/{patientId}"),
            };
            string observationId = await UpsertWithSearchIndicesAsync(observation);

            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", observationId),
                Tuple.Create("_include", "Observation:subject"),
                Tuple.Create("_include:iterate", "Patient:organization"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Observation", query, CancellationToken.None);
            SearchResult legacyResults = await legacySearchService.SearchAsync("Observation", query, CancellationToken.None);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                "Expected the iterate include search to run on Ignixa.");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            // The second hop is the whole point of the test - if legacy itself does not reach the Organization
            // there is no closure to compare and the mode-map assertion below would be satisfied by an
            // implementation that never iterates at all.
            Assert.True(
                ContainsMode(legacyResults, organizationId, SearchEntryMode.Include),
                "Legacy did not reach the Organization through :iterate; this test would not be testing iteration.");

            var seeded = new HashSet<string>(new[] { organizationId, patientId, observationId }, StringComparer.Ordinal);
            Assert.Equal(SeededModeMap(legacyResults, seeded), SeededModeMap(ignixaResults, seeded));
        }

        [Theory]
        [InlineData("date")]
        [InlineData("-date")]
        public async Task GivenASortParameterThatIsAlsoAFilter_WhenExecutedOnBothEngines_ThenIgnixaRunsTheValuedPhaseAndAgreesWithLegacy(string sortExpression)
        {
            // IsSortWithFilter: legacy emits a SortWithFilter table expression and never runs the missing-values
            // phase, because a row with no value cannot satisfy the filter anyway. Ignixa derives its phase from
            // sort direction, so before the adapter special-cased this flag an ascending first page compiled to
            // MissingPrimary and returned the complement of the correct rows. Both directions are exercised
            // because the bug only manifests on one of them.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            string tag = $"IgnixaSortFilter_{Guid.NewGuid():N}";
            var chronological = new[] { 1970, 1980, 1990 };
            var expectedIds = new List<string>();
            foreach (int year in chronological)
            {
                var observation = new Observation
                {
                    Id = Guid.NewGuid().ToString(),
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept { Text = tag },
                    Effective = new FhirDateTime(year, 1, 1),
                };
                await UpsertWithSearchIndicesAsync(observation);
                expectedIds.Add(observation.Id);
            }

            // The sort parameter (date) is ALSO a filter here - that is what sets IsSortWithFilter. _id keeps the
            // result set restricted to this test's seeds so a shared, concurrently-written table cannot page them out.
            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", string.Join(",", expectedIds)),
                Tuple.Create("date", "ge1960"),
                Tuple.Create("_sort", sortExpression),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Observation", query, CancellationToken.None);
            SearchResult legacyResults = await legacySearchService.SearchAsync("Observation", query, CancellationToken.None);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                "Expected the sort-with-filter search to run on Ignixa.");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            var seeded = new HashSet<string>(expectedIds, StringComparer.Ordinal);
            List<string> legacyMatches = ResourceIdsInResultOrder(legacyResults).Where(seeded.Contains).ToList();
            List<string> ignixaMatches = ResourceIdsInResultOrder(ignixaResults).Where(seeded.Contains).ToList();

            // Anti-vacuity + correctness: all three seeded observations have a date and satisfy the filter, so a
            // correct valued-phase query returns all three in date order. A MissingPrimary phase would return none
            // of them, which is exactly the divergence this test exists to catch - so pin the expected sequence
            // rather than only asserting the two engines agree.
            List<string> expectedOrder = sortExpression.StartsWith('-')
                ? Enumerable.Reverse(expectedIds).ToList()
                : expectedIds;

            Assert.Equal(expectedOrder, legacyMatches);
            Assert.Equal(legacyMatches, ignixaMatches);
        }

        [Fact]
        public async Task GivenASortParameterWithAMissingModifier_WhenExecutedOnBothEngines_ThenIgnixaRunsTheValuedPhaseAndAgreesWithLegacy()
        {
            // SortHasMissingModifier: "date:missing=false" makes SortRewriter skip the block that emits the
            // NotExists (missing) phase entirely, so legacy only ever runs the valued phase here too.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            string tag = $"IgnixaSortMissing_{Guid.NewGuid():N}";
            var withDate = new List<string>();
            foreach (int year in new[] { 1972, 1982 })
            {
                var observation = new Observation
                {
                    Id = Guid.NewGuid().ToString(),
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept { Text = tag },
                    Effective = new FhirDateTime(year, 2, 2),
                };
                await UpsertWithSearchIndicesAsync(observation);
                withDate.Add(observation.Id);
            }

            // Seeded without a date so ":missing=false" has something to exclude - otherwise the modifier would
            // be a no-op and the test would not distinguish the phases.
            var undated = new Observation
            {
                Id = Guid.NewGuid().ToString(),
                Status = ObservationStatus.Final,
                Code = new CodeableConcept { Text = tag },
            };
            await UpsertWithSearchIndicesAsync(undated);
            string excludedId = undated.Id;

            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", string.Join(",", withDate.Concat(new[] { excludedId }))),
                Tuple.Create("date:missing", "false"),
                Tuple.Create("_sort", "date"),
                Tuple.Create("_count", "1000"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Observation", query, CancellationToken.None);
            SearchResult legacyResults = await legacySearchService.SearchAsync("Observation", query, CancellationToken.None);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                "Expected the sort :missing search to run on Ignixa.");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            var seeded = new HashSet<string>(withDate.Concat(new[] { excludedId }), StringComparer.Ordinal);
            List<string> legacyMatches = ResourceIdsInResultOrder(legacyResults).Where(seeded.Contains).ToList();
            List<string> ignixaMatches = ResourceIdsInResultOrder(ignixaResults).Where(seeded.Contains).ToList();

            Assert.Equal(withDate, legacyMatches);
            Assert.Equal(legacyMatches, ignixaMatches);
            Assert.DoesNotContain(excludedId, ignixaMatches);
        }

        [Theory]
        [InlineData("date", false)]
        [InlineData("-date", true)]
        public async Task GivenACustomSort_WhenPagedAcrossBoundaries_ThenIgnixaSortKeysetPagingAgreesWithLegacy(string sortExpression, bool descending)
        {
            // A custom "_sort" continuation token is a different animal from the surrogate-keyset token the
            // match-all paging test walks: it carries a sort *value* as well as a surrogate id, and the phase the
            // next page must run in is not a function of direction alone - it is decided by what the token
            // carries (a token minted by the valued segment has a sort value; one minted by the missing segment
            // does not) plus the second-phase sentinel. The adapter mirrors SortRewriter's branch order to derive
            // that, and Ignixa's EmitSeekPredicate *throws* when the boundary arity does not match the phase, so a
            // wrong derivation is not a subtle mis-order - it either throws or silently re-serves page one.
            //
            // Both directions run because they enter through different first-phase branches: ascending starts in
            // the missing segment (empty here, so SearchImpl immediately runs the valued second phase inside the
            // same request), descending starts in the valued segment directly.
            //
            // A unique "code" token pins the row set to this test's seeds so a shared, concurrently-written table
            // cannot page the seeds out from under the walk - which also makes the expected order exact rather than
            // legacy-relative. It is deliberately a search-parameter filter rather than "_id": a Resource-table-only
            // predicate such as "_id" is silently dropped by the legacy generator once the missing-values phase runs
            // (see GivenAResourceIdFilterAndACustomSort_...), so it cannot be used to pin a legacy-side comparison.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var chronologicalIds = new List<string>();
            string codeSystem = "http://example.org/ignixa-sort-page";
            string codeValue = $"page_{Guid.NewGuid():N}";
            foreach (int year in new[] { 1962, 1972, 1982, 1992, 2002, 2012 })
            {
                var observation = new Observation
                {
                    Id = Guid.NewGuid().ToString(),
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept(codeSystem, codeValue),
                    Effective = new FhirDateTime(year, 3, 3),
                };

                await UpsertWithSearchIndicesAsync(observation);
                chronologicalIds.Add(observation.Id);
            }

            var baseQuery = new List<Tuple<string, string>>
            {
                Tuple.Create("code", $"{codeSystem}|{codeValue}"),
                Tuple.Create("_sort", sortExpression),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;
            fixture.IgnixaRouterLog.Clear();

            (List<string> ignixaWalk, int ignixaPages) = await WalkPagesAsync(ignixaSearchService, "Observation", baseQuery, pageSize: 2, maxPages: 8, CancellationToken.None);

            long legacyInstanceAfter = ignixaSearchService.InstanceLegacyExecutedQueryCount;
            long ignixaAfter = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            string routerLog = string.Join(" | ", fixture.IgnixaRouterLog);

            (List<string> legacyWalk, int legacyPages) = await WalkPagesAsync(legacySearchService, "Observation", baseQuery, pageSize: 2, maxPages: 8, CancellationToken.None);

            _output.WriteLine($"sort={sortExpression} ignixaPages={ignixaPages} ignixa=[{string.Join(",", ignixaWalk)}]");
            _output.WriteLine($"sort={sortExpression} legacyPages={legacyPages} legacy=[{string.Join(",", legacyWalk)}]");
            _output.WriteLine($"routerLog={routerLog}");

            // Multi-page: the walk actually crossed page boundaries, so sort continuation tokens were consumed
            // rather than a single page trivially matching.
            Assert.True(ignixaPages >= 3, $"Expected the Ignixa sorted walk to span at least three pages, got {ignixaPages}.");

            // Counter evidence: every page ran on Ignixa and none silently fell back to the legacy generator. A
            // token the router rejected would move the legacy counter instead - the exact failure the narrowed
            // continuation-token gate exists to avoid. The router log names the gate that closed.
            Assert.True(ignixaAfter > ignixaBefore, $"Expected the Ignixa sorted paging path to run. before={ignixaBefore} after={ignixaAfter}");
            Assert.True(
                legacyInstanceBefore == legacyInstanceAfter,
                $"Expected no legacy fallback, but {legacyInstanceAfter - legacyInstanceBefore} page(s) fell back. Router log: {routerLog}");

            // No duplicate: a token whose derived phase re-served page one would repeat ids here.
            Assert.Equal(ignixaWalk.Count, ignixaWalk.Distinct(StringComparer.Ordinal).Count());

            // Exact expected order, not just engine agreement: all six seeds are returned, in date order, across
            // the paged walk. A wrong seek boundary would drop or reorder rows even if both engines agreed.
            List<string> expectedOrder = descending
                ? Enumerable.Reverse(chronologicalIds).ToList()
                : chronologicalIds;

            Assert.Equal(expectedOrder, legacyWalk);
            Assert.Equal(legacyWalk, ignixaWalk);
        }

        [Fact]
        public async Task GivenAnUnsupportedSearchParameter_WhenBothEnginesIgnoreIt_ThenIgnixaStillRunsAndAgreesWithLegacy()
        {
            // An unsupported parameter used to push the whole request onto legacy. But both parsers drop the same
            // unknown parameter and both report it back on the bundle, so the row sets are identical and there is
            // nothing to protect against. The gate now turns on *disagreement* between the two drop sets, which
            // SearchOptionsFactory computes; this proves the agreeing case reaches the Ignixa engine and returns
            // legacy's answer, including the unsupported-parameter issue.
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            string codeSystem = "http://example.org/ignixa-unsupported";
            string codeValue = $"unsup_{Guid.NewGuid():N}";
            var seededIds = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var observation = new Observation
                {
                    Id = Guid.NewGuid().ToString(),
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept(codeSystem, codeValue),
                };

                await UpsertWithSearchIndicesAsync(observation);
                seededIds.Add(observation.Id);
            }

            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("code", $"{codeSystem}|{codeValue}"),

                // A second filter is required: a lone token search is intercepted by the GetResourcesByTokens
                // stored-procedure fast path before the router is ever consulted.
                Tuple.Create("status", "final"),
                Tuple.Create("thisParameterDoesNotExist", "whatever"),
                Tuple.Create("_count", "100"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;
            fixture.IgnixaRouterLog.Clear();

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Observation", query, CancellationToken.None);
            string routerLog = string.Join(" | ", fixture.IgnixaRouterLog);
            SearchResult legacyResults = await legacySearchService.SearchAsync("Observation", query, CancellationToken.None);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                $"Expected the search carrying an unsupported parameter to run on Ignixa. Router log: {routerLog}");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            // The unknown parameter is reported, not silently swallowed - the behaviour a client depends on.
            Assert.Contains(ignixaResults.UnsupportedSearchParameters, p => p.Item1 == "thisParameterDoesNotExist");

            // Anti-vacuity: the supported half of the query still filtered, and both engines returned the seeds.
            List<string> ignixaIds = ResourceIdsInResultOrder(ignixaResults);
            List<string> legacyIds = ResourceIdsInResultOrder(legacyResults);
            Assert.Equal(seededIds.OrderBy(id => id, StringComparer.Ordinal), ignixaIds.OrderBy(id => id, StringComparer.Ordinal));
            Assert.Equal(legacyIds.OrderBy(id => id, StringComparer.Ordinal), ignixaIds.OrderBy(id => id, StringComparer.Ordinal));
        }

        [Fact]
        public async Task GivenAResourceIdFilterAndACustomSort_WhenExecutedOnBothEngines_ThenIgnixaKeepsTheFilterThatLegacyDrops()
        {
            // A deliberate, documented divergence - one of the few places the cutover is not bug-for-bug.
            //
            // "_id" is a Resource-table-only predicate, so it stays in SqlRootExpression.ResourceTableExpressions
            // until ResourceColumnPredicatePushdownRewriter turns it into a leading "All" table expression. But an
            // ascending custom sort runs the missing-values phase first, and SortRewriter emits that phase as a
            // NotExists table expression at index 0 - which makes MissingSearchParamVisitor insert its own
            // unpredicated "All" ("seed with all resources so that we have something to restrict") ahead of it.
            // SqlQueryGenerator.HandleTableKindAll emits "SELECT ... FROM dbo.Resource WHERE <predicate>" with no
            // join to the preceding CTE, so that seed CTE discards the "_id" restriction produced by the CTE before
            // it, and the NotExists reads from the unrestricted seed. Net effect: legacy returns arbitrary
            // resources of the type that merely lack a sort value, ignoring "_id" entirely.
            //
            // Ignixa compiles the filter and the missing-values segment into one plan, so the filter survives. This
            // test pins that Ignixa is correct rather than merely equal, and fails loudly if legacy is ever fixed
            // (at which point the two engines agree and this test should become a plain differential).
            var fixture = (SqlServerFhirStorageTestsFixture)_fixture.Service;
            SqlServerSearchService ignixaSearchService = fixture.IgnixaSearchService;
            ISearchService legacySearchService = _fixture.SearchService;

            var seededIds = new List<string>();
            foreach (int year in new[] { 1965, 1975, 1985 })
            {
                var observation = new Observation
                {
                    Id = Guid.NewGuid().ToString(),
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept("http://example.org/ignixa-id-sort", $"idsort_{Guid.NewGuid():N}"),
                    Effective = new FhirDateTime(year, 6, 6),
                };

                await UpsertWithSearchIndicesAsync(observation);
                seededIds.Add(observation.Id);
            }

            // An observation with no effective date, so the missing-values phase is genuinely non-empty for the
            // pinned set and the ascending first phase has something legitimate to return.
            var undated = new Observation
            {
                Id = Guid.NewGuid().ToString(),
                Status = ObservationStatus.Final,
                Code = new CodeableConcept("http://example.org/ignixa-id-sort", $"idsort_{Guid.NewGuid():N}"),
            };
            await UpsertWithSearchIndicesAsync(undated);

            var pinned = new List<string>(seededIds) { undated.Id };
            var query = new List<Tuple<string, string>>
            {
                Tuple.Create("_id", string.Join(",", pinned)),
                Tuple.Create("_sort", "date"),
                Tuple.Create("_count", "100"),
            };

            long ignixaBefore = ignixaSearchService.InstanceIgnixaExecutedQueryCount;
            long legacyInstanceBefore = ignixaSearchService.InstanceLegacyExecutedQueryCount;

            SearchResult ignixaResults = await ignixaSearchService.SearchAsync("Observation", query, CancellationToken.None);
            SearchResult legacyResults = await legacySearchService.SearchAsync("Observation", query, CancellationToken.None);

            Assert.True(
                ignixaSearchService.InstanceIgnixaExecutedQueryCount > ignixaBefore,
                "Expected the _id-filtered sorted search to run on Ignixa.");
            Assert.Equal(legacyInstanceBefore, ignixaSearchService.InstanceLegacyExecutedQueryCount);

            List<string> ignixaIds = ResourceIdsInResultOrder(ignixaResults);
            List<string> legacyIds = ResourceIdsInResultOrder(legacyResults);

            // Ignixa honours "_id": exactly the pinned rows, ascending sort putting the missing-value row first.
            var expected = new List<string> { undated.Id };
            expected.AddRange(seededIds);
            Assert.Equal(expected, ignixaIds);

            // Legacy drops it: it returns rows outside the pinned set. Asserted rather than tolerated so the
            // divergence is visible in the suite instead of hiding behind a "where seeded.Contains" filter.
            var pinnedSet = new HashSet<string>(pinned, StringComparer.Ordinal);
            Assert.Contains(legacyIds, id => !pinnedSet.Contains(id));
        }

        /// <summary>
        /// Walks a search across continuation-token pages, returning the concatenated match ids and the page count.
        /// </summary>
        private static async Task<(List<string> Ids, int Pages)> WalkPagesAsync(
            ISearchService service,
            string resourceType,
            IReadOnlyList<Tuple<string, string>> baseQuery,
            int pageSize,
            int maxPages,
            CancellationToken cancellationToken)
        {
            var ids = new List<string>();
            string continuation = null;
            int pages = 0;

            while (pages < maxPages)
            {
                var query = new List<Tuple<string, string>>(baseQuery)
                {
                    Tuple.Create("_count", pageSize.ToString(CultureInfo.InvariantCulture)),
                };

                if (continuation != null)
                {
                    query.Add(Tuple.Create(
                        Core.Features.KnownQueryParameterNames.ContinuationToken,
                        ContinuationTokenEncoder.Encode(continuation)));
                }

                SearchResult page = await service.SearchAsync(resourceType, query, cancellationToken);
                pages++;
                ids.AddRange(ResourceIdsInResultOrder(page));

                continuation = page.ContinuationToken;
                if (string.IsNullOrEmpty(continuation))
                {
                    break;
                }
            }

            return (ids, pages);
        }

        /// <summary>
        /// Upserts a resource with real search indices extracted by a real <see cref="TypedElementSearchIndexer"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="FhirStorageTestsFixture"/> resolves <see cref="ISearchIndexer"/> from the storage fixture and,
        /// because the SQL fixture does not supply one, falls back to a substitute that only ever produces a
        /// SearchParameter "url" index. Resources upserted through the mediator therefore land in dbo.Resource with
        /// no dbo.ReferenceSearchParam rows at all, which makes every _include and _revinclude return zero included
        /// rows on BOTH engines - so an include differential written against mediator-seeded data agrees vacuously
        /// and would keep agreeing if the Ignixa include emitter were removed entirely.
        ///
        /// Seeding through this path instead writes the reference index rows, so include assertions are real. It is
        /// deliberately scoped to this file rather than fixed by registering a real indexer on the shared fixture:
        /// that would silently change the indexed data under every other SQL integration test in the assembly.
        /// </remarks>
        private async Task<string> UpsertWithSearchIndicesAsync(Resource resource)
        {
            ISearchIndexer indexer = await GetSearchIndexerAsync();

            resource.Id ??= Guid.NewGuid().ToString();
            resource.Meta ??= new Meta();
            resource.Meta.LastUpdated = DateTimeOffset.UtcNow;

            ResourceElement resourceElement = resource.ToResourceElement();
            var rawResource = new RawResource(resource.ToJson(), FhirResourceFormat.Json, isMetaSet: false);
            IReadOnlyCollection<SearchIndexEntry> searchIndices = indexer.Extract(resourceElement);
            MarkMinAndMaxSortValues(searchIndices);

            var wrapper = new ResourceWrapper(
                resourceElement,
                rawResource,
                new ResourceRequest("PUT"),
                deleted: false,
                searchIndices,
                Substitute.For<CompartmentIndices>(),
                new List<KeyValuePair<string, string>>(),
                _fixture.SearchParameterDefinitionManager.GetSearchParameterHashForResourceType(resource.TypeName));

            await _fixture.DataStore.UpsertAsync(
                new ResourceWrapperOperation(wrapper, true, true, null, false, false, bundleResourceContext: null),
                CancellationToken.None);

            return resource.Id;
        }

        /// <summary>
        /// Mirrors <c>ResourceWrapperFactory.ExtractMinAndMaxValues</c>, which this file bypasses by building the
        /// <see cref="ResourceWrapper"/> directly.
        /// </summary>
        /// <remarks>
        /// The SQL sort expressions do not join the search-param index table on value alone - they additionally
        /// require IsMin = 1 (ascending) or IsMax = 1 (descending), which is how a resource with several values for
        /// the sort parameter contributes exactly one sort key. Those two bit columns are populated from
        /// <see cref="ISupportSortSearchValue"/> flags that only the production wrapper factory sets. Without this
        /// pass the rows are written with IsMin = IsMax = 0, so a *filter* on the parameter matches while a *sort*
        /// on the same parameter silently returns nothing - on both engines, which would make a sort differential
        /// agree vacuously.
        /// </remarks>
        private static void MarkMinAndMaxSortValues(IReadOnlyCollection<SearchIndexEntry> searchIndices)
        {
            var minValues = new Dictionary<Uri, ISupportSortSearchValue>();
            var maxValues = new Dictionary<Uri, ISupportSortSearchValue>();

            foreach (SearchIndexEntry entry in searchIndices)
            {
                if (entry.Value is not ISupportSortSearchValue currentValue ||
                    entry.SearchParameter.SortStatus == SortParameterStatus.Disabled)
                {
                    continue;
                }

                if (!minValues.TryGetValue(entry.SearchParameter.Url, out ISupportSortSearchValue existingMin) ||
                    currentValue.CompareTo(existingMin, ComparisonRange.Min) < 0)
                {
                    minValues[entry.SearchParameter.Url] = currentValue;
                }

                if (!maxValues.TryGetValue(entry.SearchParameter.Url, out ISupportSortSearchValue existingMax) ||
                    currentValue.CompareTo(existingMax, ComparisonRange.Max) > 0)
                {
                    maxValues[entry.SearchParameter.Url] = currentValue;
                }
            }

            foreach (ISupportSortSearchValue value in minValues.Values)
            {
                value.IsMin = true;
            }

            foreach (ISupportSortSearchValue value in maxValues.Values)
            {
                value.IsMax = true;
            }
        }

        /// <summary>
        /// Builds the real search indexer once per process. Construction reflects over every
        /// <see cref="ITypedElementToSearchValueConverter"/> and starts a <see cref="CodeSystemResolver"/>, which is
        /// slow enough to matter if repeated per test, and the result is immutable, so it is cached.
        /// </summary>
        private async Task<ISearchIndexer> GetSearchIndexerAsync()
        {
            if (_searchIndexer != null)
            {
                return _searchIndexer;
            }

            await _searchIndexerLock.WaitAsync();
            try
            {
                if (_searchIndexer == null)
                {
                    var types = typeof(ITypedElementToSearchValueConverter)
                        .Assembly
                        .GetTypes()
                        .Where(x => typeof(ITypedElementToSearchValueConverter).IsAssignableFrom(x) && !x.IsAbstract && !x.IsInterface);

                    var referenceSearchValueParser = new ReferenceSearchValueParser(
                        new Microsoft.Health.Fhir.Core.Features.Context.FhirRequestContextAccessor(),
                        new FhirServerInstanceConfiguration());
                    var codeSystemResolver = new CodeSystemResolver(ModelInfoProvider.Instance);
                    await codeSystemResolver.StartAsync(CancellationToken.None);

                    var converters = new List<ITypedElementToSearchValueConverter>();
                    foreach (Type type in types.Where(t => t.Name != nameof(FhirTypedElementToSearchValueConverterManager.ExtensionConverter)))
                    {
                        converters.Add((ITypedElementToSearchValueConverter)Mock.TypeWithArguments(type, referenceSearchValueParser, codeSystemResolver));
                    }

                    _searchIndexer = new TypedElementSearchIndexer(
                        _fixture.SupportedSearchParameterDefinitionManager,
                        new FhirTypedElementToSearchValueConverterManager(converters),
                        Substitute.For<IReferenceToElementResolver>(),
                        ModelInfoProvider.Instance,
                        NullLogger<TypedElementSearchIndexer>.Instance);
                }
            }
            finally
            {
                _searchIndexerLock.Release();
            }

            return _searchIndexer;
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

        private static List<string> OrderedResourceIdVersions(SearchResult results, string resourceId)
        {
            return results.Results
                .Where(r => string.Equals(r.Resource.ResourceId, resourceId, StringComparison.Ordinal))
                .Select(r => $"{r.Resource.ResourceId}/{r.Resource.Version}")
                .OrderBy(idVersion => idVersion, StringComparer.Ordinal)
                .ToList();
        }
    }
}
