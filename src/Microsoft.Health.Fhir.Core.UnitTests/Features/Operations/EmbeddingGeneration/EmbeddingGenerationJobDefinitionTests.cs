// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Linq;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.EmbeddingGeneration;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Operations.EmbeddingGeneration
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.IndexAndReindex)]
    public class EmbeddingGenerationJobDefinitionTests
    {
        [Fact]
        public void GivenEmbeddingGenerationJobDefinition_WhenSerializedAndDeserialized_ThenValuesRoundTrip()
        {
            // Arrange
            var definition = new EmbeddingGenerationJobDefinition(
                JobType.EmbeddingGenerationProcessing,
                resourceTypeId: 42,
                startSurrogateId: 100,
                endSurrogateId: 200);

            // Act
            string serializedDefinition = JsonConvert.SerializeObject(definition);
            var deserializedDefinition = JsonConvert.DeserializeObject<EmbeddingGenerationJobDefinition>(serializedDefinition);

            // Assert
            Assert.Equal((int)JobType.EmbeddingGenerationProcessing, deserializedDefinition.TypeId);
            Assert.Equal((short)42, deserializedDefinition.ResourceTypeId);
            Assert.Equal(100, deserializedDefinition.StartSurrogateId);
            Assert.Equal(200, deserializedDefinition.EndSurrogateId);
        }

        [Fact]
        public void GivenEmbeddingGenerationJobDefinition_WhenCreated_ThenTypeIdIsTheProcessingJobType()
        {
            // Arrange
            var definition = new EmbeddingGenerationJobDefinition(
                JobType.EmbeddingGenerationProcessing,
                resourceTypeId: 42,
                startSurrogateId: 100,
                endSurrogateId: 200);

            // Act
            int typeId = definition.TypeId;

            // Assert
            Assert.Equal((int)JobType.EmbeddingGenerationProcessing, typeId);
            Assert.NotEqual((int)QueueType.EmbeddingGeneration, typeId);
        }

        [Fact]
        public void GivenEmbeddingGenerationJobDefinition_WhenSerialized_ThenItUsesStableNonPhiWireFormat()
        {
            // Arrange
            var definition = new EmbeddingGenerationJobDefinition(
                JobType.EmbeddingGenerationProcessing,
                resourceTypeId: 42,
                startSurrogateId: 100,
                endSurrogateId: 200);

            // Act
            var serializedDefinition = JObject.Parse(JsonConvert.SerializeObject(definition));

            // Assert
            Assert.Equal(
                new[] { "endSurrogateId", "resourceTypeId", "startSurrogateId", "typeId" },
                serializedDefinition.Properties().Select(property => property.Name).OrderBy(name => name));
        }
    }
}
