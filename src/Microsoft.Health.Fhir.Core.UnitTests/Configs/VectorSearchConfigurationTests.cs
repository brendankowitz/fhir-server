// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Configs
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Operations)]
    public class VectorSearchConfigurationTests
    {
        /// <summary>
        /// Verifies that the default vector search configuration is disabled and uses synchronous persistence.
        /// </summary>
        [Fact]
        public void GivenDefaultConfiguration_VectorSearchIsDisabledAndSynchronous()
        {
            var config = new CoreFeatureConfiguration();

            Assert.NotNull(config.VectorSearch);
            Assert.False(config.VectorSearch.Enabled);
            Assert.False(config.VectorSearch.Suspended);
            Assert.False(config.VectorSearch.EnableApproximateVectorIndex);
            Assert.Equal(EmbeddingPersistenceMode.Synchronous, config.VectorSearch.EmbeddingPersistenceMode);
            Assert.Equal(1536, config.VectorSearch.EmbeddingDimensions);
            Assert.Equal(200, config.VectorSearch.DefaultK);
            Assert.Equal(1000, config.VectorSearch.MaxK);
            Assert.Equal(2.0, config.VectorSearch.OversamplingFactor);
            Assert.Equal(4096, config.VectorSearch.MaxQueryTextLength);
            Assert.Equal(500, config.VectorSearch.JobBatchSize);
        }
    }
}
