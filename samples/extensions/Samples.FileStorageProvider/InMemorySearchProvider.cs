// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;

namespace SamplesFileStorageProvider
{
    public class InMemorySearchProvider : SearchService
    {
        private readonly IFhirDataStore _fhirDataStore;

        public InMemorySearchProvider(ISearchOptionsFactory searchOptionsFactory, IFhirDataStore fhirDataStore)
            : base(searchOptionsFactory, fhirDataStore)
        {
            _fhirDataStore = fhirDataStore;
        }

        protected override async Task<SearchResult> SearchInternalAsync(SearchOptions searchOptions, CancellationToken cancellationToken)
        {
            (ResourceLocation, IReadOnlyCollection<SearchIndexEntry>)[] results;

            if (searchOptions.Expression != null)
            {
                var interpreter = new SearchQueryInterpreter();

                var searchFunc = searchOptions.Expression.AcceptVisitor(interpreter, default);

                results = searchFunc.Invoke(InMemoryIndex.Index.Values).ToArray();
            }
            else
            {
                results = InMemoryIndex.Index.Values.Take(10).ToArray();
            }

            var resources = await Task.WhenAll(results.Select(async x => new SearchResultEntry(await _fhirDataStore.GetAsync(new ResourceKey(x.Item1.ResourceType, x.Item1.ResourceId), cancellationToken))));

            return new SearchResult(
                resources,
                searchOptions.UnsupportedSearchParams,
                searchOptions.UnsupportedSortingParams,
                null);
        }

        protected override Task<SearchResult> SearchHistoryInternalAsync(SearchOptions searchOptions, CancellationToken cancellationToken)
        {
            throw new System.NotImplementedException();
        }
    }
}
