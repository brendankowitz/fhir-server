// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;

namespace Microsoft.Health.Fhir.SqlServer.Features.Storage.TvpRowGeneration
{
    internal class VectorSearchParamListRowGenerator : MergeSearchParameterRowGenerator<VectorSearchValue, VectorSearchParamListRow>
    {
        private readonly IVectorSearchCapabilityProvider _capabilityProvider;

        public VectorSearchParamListRowGenerator(
            SqlServerFhirModel model,
            SearchParameterToSearchValueTypeMap searchParameterTypeMap,
            IVectorSearchCapabilityProvider capabilityProvider)
            : base(model, searchParameterTypeMap)
        {
            _capabilityProvider = EnsureArg.IsNotNull(capabilityProvider, nameof(capabilityProvider));
        }

        internal override bool TryGenerateRow(short resourceTypeId, long resourceSurrogateId, short searchParamId, VectorSearchValue searchValue, HashSet<VectorSearchParamListRow> results, out VectorSearchParamListRow row)
        {
            if (searchValue.Embedding == null)
            {
                row = default;
                return false;
            }

            if (!_capabilityProvider.IsVectorSearchAvailable)
            {
                row = default;
                return false;
            }

            if (searchValue.ChunkOrdinal < short.MinValue || searchValue.ChunkOrdinal > short.MaxValue)
            {
                row = default;
                return false;
            }

            short chunkOrdinal = (short)searchValue.ChunkOrdinal;

            // PKC_VectorSearchParam is (ResourceTypeId, ResourceSurrogateId, SearchParamId, ChunkOrdinal);
            // a duplicate key fails the whole merge. VectorSearchParamListRow is a generated struct with no
            // Equals override and a Stream member (reference equality), so the real row can never dedupe.
            // Add a key-only sentinel instead - reference members null, non-key value members zeroed - so
            // default ValueType equality discriminates on exactly the primary key.
            var dedupKey = new VectorSearchParamListRow(resourceTypeId, resourceSurrogateId, searchParamId, chunkOrdinal, null, 0, null);

            if (results != null && !results.Add(dedupKey))
            {
                row = default;
                return false;
            }

            row = new VectorSearchParamListRow(
                resourceTypeId,
                resourceSurrogateId,
                searchParamId,
                chunkOrdinal,
                JsonSerializer.Serialize(searchValue.Embedding),
                _capabilityProvider.EmbeddingModelId,
                new MemoryStream(searchValue.SourceTextHash));

            return true;
        }
    }
}
