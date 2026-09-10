// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Persistence;

namespace Microsoft.Health.Fhir.SqlServer.Features.Storage
{
    internal interface IVectorSearchParamStore
    {
        /// <summary>
        /// Reads the complete stored vector metadata, across every search parameter, for resources in
        /// the specified surrogate ID range. Replacement is a whole-resource operation, not a partial
        /// search-parameter update.
        /// </summary>
        Task<IReadOnlyDictionary<long, IReadOnlyList<VectorSearchParamState>>> GetStateAsync(
            short resourceTypeId,
            long startSurrogateId,
            long endSurrogateId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Deletes complete vector rows whose search parameter is not in the supplied vector-enabled set.
        /// Deletion is bounded per statement and per invocation. An empty set is treated as unavailable
        /// configuration and deletes nothing.
        /// </summary>
        Task<IReadOnlyDictionary<short, long>> DeleteRowsForDisabledSearchParametersAsync(
            IReadOnlyCollection<short> vectorEnabledSearchParamIds,
            CancellationToken cancellationToken);

        /// <summary>
        /// Completely replaces vector rows for every listed resource. All existing vector rows for each
        /// resource are deleted, and only rows generated from the supplied resources are reinserted.
        /// Callers must supply the complete vector set for every resource they list, and every supplied
        /// resource must be of the indicated resource type.
        /// </summary>
        Task ReplaceAsync(
            short resourceTypeId,
            IReadOnlyList<ResourceWrapper> resources,
            CancellationToken cancellationToken);
    }
}
