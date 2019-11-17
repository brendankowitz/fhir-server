// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace SamplesFileStorageProvider
{
    public class ResourceLocation
    {
        public ResourceLocation(string resourceId, string resourceType, string version, string filePath)
        {
            ResourceId = resourceId;
            ResourceType = resourceType;
            Version = version;
            FilePath = filePath;
        }

        public string ResourceId { get; set; }

        public string ResourceType { get; set; }

        public string Version { get; }

        public string FilePath { get; set; }
    }
}
