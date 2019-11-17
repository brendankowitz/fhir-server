// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Search;

namespace SamplesFileStorageProvider
{
    public static class InMemoryIndex
    {
        public static readonly ConcurrentDictionary<string, (ResourceLocation Location, IReadOnlyCollection<SearchIndexEntry> Index)> Index
            = new ConcurrentDictionary<string, (ResourceLocation, IReadOnlyCollection<SearchIndexEntry>)>();
    }
}
