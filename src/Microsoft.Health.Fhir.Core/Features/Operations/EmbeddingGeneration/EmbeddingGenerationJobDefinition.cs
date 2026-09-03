// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.JobManagement;
using Newtonsoft.Json;

namespace Microsoft.Health.Fhir.Core.Features.Operations.EmbeddingGeneration
{
    internal class EmbeddingGenerationJobDefinition : IJobData
    {
        public EmbeddingGenerationJobDefinition(
            JobType jobType,
            short resourceTypeId,
            long startSurrogateId,
            long endSurrogateId)
        {
            TypeId = (int)jobType;
            ResourceTypeId = resourceTypeId;
            StartSurrogateId = startSurrogateId;
            EndSurrogateId = endSurrogateId;
        }

        [JsonConstructor]
        protected EmbeddingGenerationJobDefinition()
        {
        }

        [JsonProperty(JobRecordProperties.TypeId)]
        public int TypeId { get; set; }

        [JsonProperty(JobRecordProperties.ResourceTypeId)]
        public short ResourceTypeId { get; private set; }

        [JsonProperty(JobRecordProperties.StartSurrogateId)]
        public long StartSurrogateId { get; private set; }

        [JsonProperty(JobRecordProperties.EndSurrogateId)]
        public long EndSurrogateId { get; private set; }
    }
}
