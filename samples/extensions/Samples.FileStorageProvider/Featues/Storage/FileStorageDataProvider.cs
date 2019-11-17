// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Exceptions;
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
            _folderPath = settings.Value.FolderPath;
            _searchIndexer = searchIndexer;
            _modelInfoProvider = modelInfoProvider;
        }

        public async Task<UpsertOutcome> UpsertAsync(ResourceWrapper resource, WeakETag weakETag, bool allowCreate, bool keepHistory, CancellationToken cancellationToken)
        {
            var create = true;            string entityPath = Path.Combine(_folderPath, resource.ResourceTypeName + "_" + resource.ResourceId + ".json");            if (allowCreate && !InMemoryIndex.Index.ContainsKey($"{resource.ResourceTypeName}_{resource.ResourceId}"))            {                try                {
                    // Optimize for insert
                    using (FileStream file = File.Open(entityPath, FileMode.CreateNew))                    using (var writer = new StreamWriter(file))                    {                        await writer.WriteAsync(resource.RawResource.Data);                    }                }                catch (IOException)                {                    Debug.WriteLine("Resource {0} already exists", resource.ResourceId);                    create = false;                }            }            else            {                create = false;                if (!File.Exists(entityPath))
                {
                    throw new ResourceNotFoundException("This resource cannot be updated.");
                }            }            if (!create)
            {
                // Optimize for insert
                using (FileStream file = File.Open(entityPath, FileMode.Create))
                using (var writer = new StreamWriter(file))
                {
                    await writer.WriteAsync(resource.RawResource.Data);
                }
            }            UpdateIndex(JObject.Parse(resource.RawResource.Data), entityPath);            var wrapper = new ResourceWrapper(
                    resource.ResourceId,
                    resource.Version ?? "1",
                    resource.ResourceTypeName,
                    resource.RawResource,
                    resource.Request,
                    new FileInfo(entityPath).LastWriteTime.ToUniversalTime(),
                    resource.IsDeleted,
                    null,
                    null,
                    null);            return new UpsertOutcome(wrapper, create ? SaveOutcomeType.Created : SaveOutcomeType.Updated);
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

                JObject token;

                if (IsBundle(raw))
                {
                    token = raw.SelectToken($"$.entry[?(@.resource.id == '{key.Id}')].resource") as JObject;
                }
                else
                {
                    token = raw;
                }

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

            throw new ResourceNotFoundException("Resource not found");
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
                builder.AddRestInteraction(resource, TypeRestfulInteraction.Create);
                builder.AddRestInteraction(resource, TypeRestfulInteraction.Update);
                builder.AddRestInteraction(resource, TypeRestfulInteraction.SearchType);

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

                foreach (var file in files)
                {
                    var raw = JObject.Parse(File.ReadAllText(file));
                    IEnumerable<JToken> selectTokens;

                    if (IsBundle(raw))
                    {
                        selectTokens = raw.SelectTokens("$..resource");
                    }
                    else
                    {
                        selectTokens = new JToken[] { raw };
                    }

                    Parallel.ForEach(selectTokens, resource =>
                    {
                        UpdateIndex((JObject)resource, file);
                    });
                }
            }
        }

        private void UpdateIndex(JObject resource, string file)
        {
            var currentNode = FhirJsonNode.Create(resource)
                            .ToTypedElement(_modelInfoProvider.StructureDefinitionSummaryProvider);

            ResourceElement resourceElement = currentNode.ToResourceElement();
            var indexes = _searchIndexer.Extract(resourceElement);

            var value = (new ResourceLocation(resourceElement.Id, resourceElement.InstanceType, resourceElement.VersionId, file), indexes);

            InMemoryIndex.Index.AddOrUpdate(
                $"{resourceElement.InstanceType}_{resourceElement.Id}",
                value,
                (_, __) => value);
        }

        private bool IsBundle(JObject obj)
        {
            return obj["resourceType"].Value<string>() == "Bundle";
        }
    }
}
