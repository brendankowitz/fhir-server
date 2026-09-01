// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;

namespace Microsoft.Health.Fhir.Core.Features.Search.VectorSearch
{
    /// <summary>
    /// Decorates an embedding client with a configuration-controlled suspension gate.
    /// </summary>
    public class SuspendableEmbeddingClient : IEmbeddingClient
    {
        private readonly IEmbeddingClient _inner;
        private readonly IOptions<CoreFeatureConfiguration> _coreFeatures;

        /// <summary>
        /// Initializes a new instance of the <see cref="SuspendableEmbeddingClient"/> class.
        /// </summary>
        /// <param name="inner">The embedding client to decorate.</param>
        /// <param name="coreFeatures">The core feature configuration.</param>
        public SuspendableEmbeddingClient(IEmbeddingClient inner, IOptions<CoreFeatureConfiguration> coreFeatures)
        {
            EnsureArg.IsNotNull(inner, nameof(inner));
            EnsureArg.IsNotNull(coreFeatures, nameof(coreFeatures));

            _inner = inner;
            _coreFeatures = coreFeatures;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, bool tightBudget, CancellationToken cancellationToken)
        {
            if (_coreFeatures.Value.VectorSearch.Suspended)
            {
                throw new EmbeddingUnavailableException();
            }

            return _inner.EmbedAsync(inputs, tightBudget, cancellationToken);
        }
    }
}
