// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Conformance.Models;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.ValueSets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SamplesFileStorageProvider
{
    public sealed class FileStorageDataProvider : IFhirDataStore, IProvideCapability, IStartable
    {
        private readonly string _folderPath;
        private readonly ISearchIndexer _searchIndexer;
        private readonly IModelInfoProvider _modelInfoProvider;
        private readonly JsonSerializer _serializer = new JsonSerializer();

        public FileStorageDataProvider(
            IOptions<FileStorageSettings> settings,
            ISearchIndexer searchIndexer,
            IModelInfoProvider modelInfoProvider)
        {
            _folderPath = settings.Value.FolderPath ?? @"C:\tmp\fhir-r4";
            _searchIndexer = searchIndexer;
            _modelInfoProvider = modelInfoProvider;
        }

        public Task<UpsertOutcome> UpsertAsync(ResourceWrapper resource, WeakETag weakETag, bool allowCreate, bool keepHistory, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<ResourceWrapper> GetAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            InMemoryIndex.Index.TryGetValue($"{key.ResourceType}_{key.Id}", out var entry);

            if (entry.Location != null)
            {
                JObject raw;
                using (var jsonReader = new JsonTextReader(new StreamReader(File.OpenRead(entry.Location.FilePath))))
                {
                    raw = _serializer.Deserialize<JObject>(jsonReader);
                }

                var token = raw.SelectToken($"$.entry[?(@.resource.id == '{key.Id}')].resource") as JObject;

                return Task.FromResult(new ResourceWrapper(
                    key.Id,
                    key.VersionId ?? "1",
                    key.ResourceType,
                    new RawResource(token.ToString(), FhirResourceFormat.Json),
                    new ResourceRequest(HttpMethod.Post),
                    new FileInfo(entry.Location.FilePath).LastWriteTime.ToUniversalTime(),
                    false,
                    null,
                    null,
                    null));
            }

            return null;
        }

        public Task HardDeleteAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ITransactionScope BeginTransaction()
        {
            throw new NotImplementedException();
        }

        public void Build(ICapabilityStatementBuilder builder)
        {
            foreach (var resource in InMemoryIndex.Index.Select(x => x.Value.Location.ResourceType).Distinct())
            {
                builder.AddRestInteraction(resource, TypeRestfulInteraction.Read);

                builder.AddSearchParams(resource, InMemoryIndex.Index
                    .Where(x => x.Value.Location.ResourceType == resource)
                    .SelectMany(x => x.Value.Index)
                    .Select(x => x.SearchParameter)
                    .Select(x => new SearchParamComponent
                    {
                        Name = x.Name,
                        Type = x.Type,
                        Definition = x.Url,
                        Documentation = x.Description,
                    }));
            }
        }

        public void Start()
        {
            lock (InMemoryIndex.Index)
            {
                if (InMemoryIndex.Index.Any())
                {
                    return;
                }

                var files = Directory.GetFiles(_folderPath, "*.json", SearchOption.TopDirectoryOnly);

                foreach (var file in files.Take(1))
                {
                    var raw = JObject.Parse(File.ReadAllText(file));

                    IEnumerable<JToken> selectTokens = raw.SelectTokens("$..resource");

                    Parallel.ForEach(selectTokens, resource =>
                    {
                        var currentNode = FhirJsonNode.Create((JObject)resource)
                            .ToTypedElement(_modelInfoProvider.StructureDefinitionSummaryProvider);

                        ResourceElement resourceElement = currentNode.ToResourceElement();
                        var indexes = _searchIndexer.Extract(resourceElement);

                        InMemoryIndex.Index.TryAdd(
                            $"{resourceElement.InstanceType}_{resourceElement.Id}",
                            (new ResourceLocation(resourceElement.Id, resourceElement.InstanceType, resourceElement.VersionId, file), indexes));
                    });
                }
            }
        }
    }
}
