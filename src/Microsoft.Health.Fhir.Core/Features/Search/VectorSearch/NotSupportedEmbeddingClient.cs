// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Search;

namespace Microsoft.Health.Fhir.Core.Features.Search.VectorSearch
{
    /// <summary>
    /// Rejects embedding requests when vector search is not enabled.
    /// </summary>
    public class NotSupportedEmbeddingClient : IEmbeddingClient
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, bool tightBudget, CancellationToken cancellationToken)
        {
            throw new SearchOperationNotSupportedException(global::Microsoft.Health.Fhir.Core.Resources.VectorSearchNotEnabled);
        }
    }
}
