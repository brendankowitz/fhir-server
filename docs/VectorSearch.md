# Vector search

Vector search lets a FHIR search parameter match resources by semantic similarity rather than exact text. The server extracts text selected by a `SearchParameter`, creates embeddings through Azure AI Foundry, persists the embeddings in SQL, and returns results in relevance order.

This feature is intended for controlled preview use. It is SQL Server storage-provider functionality only and requires an Azure SQL engine with native `VECTOR` support. Schema V117 makes `VECTOR` a hard requirement: SQL Server 2019 and SQL Server 2022 instances cannot upgrade to V117. Do not enable automatic schema upgrade to V117 on those engines.

## Prerequisites

* Azure SQL Database with `VECTOR` support.
* Schema V117 or later.
* An Azure AI Foundry embedding deployment reachable by the FHIR server.
* A managed identity or other `DefaultAzureCredential` identity authorized to invoke that deployment.
* Embedding dimensions configured to match the deployment.

## Configuration

Vector search settings are under `FhirServer:VectorSearch`. In environment-variable or App Service form, use double underscores, for example `FhirServer__VectorSearch__Enabled`.

| Setting | Default | Description |
| --- | ---: | --- |
| `Enabled` | `false` | Enables vector indexing and vector queries. |
| `Suspended` | `false` | Pauses all embedding calls, including query embedding, so vector queries return 503 while suspended. Stored vectors are retained and become searchable again after suspension is lifted. Changing this setting requires a restart or rolling redeployment. |
| `EmbeddingPersistenceMode` | `Synchronous` | `Synchronous` embeds during a resource write. `Asynchronous` relies on the background path. |
| `EmbeddingEndpoint` | none | Azure AI Foundry endpoint URI. |
| `EmbeddingDeploymentName` | none | Name of the embedding deployment. |
| `EmbeddingDimensions` | `1536` | Number of values returned by the embedding model. This must match the deployment and persisted model. |
| `EmbeddingModelVersion` | `1` | Operator-controlled identity for the deployed model version. Change it when the model behind a deployment changes. |
| `ChunkSizeTokens` | `1000` | Target size of each embedded text chunk. Valid values are 1 through 2048. |
| `ChunkOverlapTokens` | `100` | Tokens repeated between adjacent chunks. It must be less than `ChunkSizeTokens`. |
| `MaxChunksPerSearchParameter` | `64` | Maximum chunks retained for one resource/search-parameter pair. Remaining text is dropped. |
| `DefaultK` | `200` | Default nearest-neighbor count. |
| `MaxK` | `1000` | Maximum accepted `_vectorK`. |
| `OversamplingFactor` | `2.0` | Candidate multiplier used by the approximate path. |
| `MaxQueryTextLength` | `4096` | Maximum vector query text length. |
| `JobBatchSize` | `500` | Maximum SQL transactions assigned to one background embedding job. |
| `EnableApproximateVectorIndex` | `false` | Allows the preview approximate index path when a compatible index is present. |

For example:

```json
{
  "FhirServer": {
    "VectorSearch": {
      "Enabled": true,
      "EmbeddingPersistenceMode": "Asynchronous",
      "EmbeddingEndpoint": "https://example.services.ai.azure.com",
      "EmbeddingDeploymentName": "text-embedding-3-small",
      "EmbeddingDimensions": 1536,
      "EmbeddingModelVersion": "1",
      "ChunkSizeTokens": 1000,
      "ChunkOverlapTokens": 100,
      "MaxChunksPerSearchParameter": 64
    }
  }
}
```

`Enabled` and `Suspended` are read once at process startup. Changing either setting requires a restart or rolling redeployment; neither is a live kill switch. `Suspended=true` prevents new query, write, and background embedding calls without deleting vectors already stored in SQL. Because every vector query must embed its query text, vector queries are unavailable while suspension is active; the stored vectors become searchable again after embedding is resumed. `Enabled=false` makes queries using vector search parameters unsupported instead of treating them as unfiltered searches.

## Define a vector search parameter

Vector search parameters are ordinary FHIR `SearchParameter` resources of type `special`. Add the `vector-search-config` extension:

```json
{
  "resourceType": "SearchParameter",
  "id": "Observation-clinical-note",
  "url": "http://example.org/fhir/SearchParameter/Observation-clinical-note",
  "name": "clinicalNote",
  "code": "clinical-note",
  "status": "active",
  "base": [ "Observation" ],
  "type": "special",
  "expression": "Observation.note.text",
  "extension": [
    {
      "url": "http://microsoft.com/fhir/StructureDefinition/vector-search-config",
      "extension": [
        {
          "url": "extractionPolicy",
          "valueCode": "concatenate"
        },
        {
          "url": "maxInputTokens",
          "valueInteger": 8000
        }
      ]
    }
  ]
}
```

`extractionPolicy` controls how values selected by the expression become embedding inputs:

* `firstValue` uses only the first extracted value.
* `concatenate` joins all extracted values before chunking. This is the default.
* `perValueRow` preserves extracted values as separate inputs before chunking.

`maxInputTokens` limits the total extracted text accepted for that search parameter. The default is 8000 and the value must be positive.

### Chunking and coverage

The server applies `MaxInputTokens` before chunking. `ChunkSizeTokens` and `ChunkOverlapTokens` improve matching around passage boundaries, but chunking alone does not provide whole-document coverage. Text beyond `MaxInputTokens`, and chunks beyond `MaxChunksPerSearchParameter`, are not embedded.

The embedding model identity includes `EmbeddingDeploymentName`, `EmbeddingModelVersion`, `ChunkSizeTokens`, and `ChunkOverlapTokens`. `EmbeddingDimensions` must match the dimension already recorded for that identity or initialization fails. Changing the model version or either chunk setting creates a new model identity. A background job re-embeds a resource when it processes that resource and sees that the stored model identity is stale.

Changing model identity does **not** currently reset the persisted watchdog transaction watermark or automatically enqueue a full historical sweep. Resources already behind the watermark are not re-embedded solely because the setting changed. Operators must arrange for those resources to be processed again if a complete migration is required. Such a migration is expensive in model calls, SQL work, and elapsed time, and should be planned and monitored instead of treating these settings as routine tuning knobs.

Vector search parameters flow through the normal capability-statement machinery and appear in `CapabilityStatement.rest.resource.searchParam`.

## Embedding persistence

### Synchronous

With `EmbeddingPersistenceMode=Synchronous`, normal API writes attempt embedding before the SQL merge transaction. A failed embedding call fails the write rather than persisting a resource without its vectors.

When the server is configured for asynchronous persistence, a client can request synchronous persistence for one write:

```http
Prefer: embedding-persistence=synchronous
```

When the server honors the preference it returns:

```http
Preference-Applied: embedding-persistence=synchronous
```

Treat the response header as the signal that synchronous embedding was applied.

### Asynchronous

With `EmbeddingPersistenceMode=Asynchronous`, the resource write completes without calling the embedding endpoint. `VectorizationWatchdog` enqueues SQL transaction ranges, and `EmbeddingGenerationJob` extracts text and writes changed vectors. Results are eventually searchable after the watchdog and queue workers complete.

`$import` never embeds inline. In asynchronous mode, imported resources are vectorized by the watchdog on its normal cadence. Import completion therefore does not imply vector-search readiness.

The background job compares extracted text hashes and embedding model identity with stored state. It repairs changed resource text when the watchdog processes the corresponding transaction. It also replaces stale model identities in jobs that cover a resource, but a model or chunk-setting change does not itself schedule historical resources behind the watchdog watermark.

### Runtime background control

The live control for background embedding spend is the `VectorizationWatchdog.IsEnabled` row in `dbo.Parameters`. Set its numeric value to `0` to stop new backfill jobs without restarting the service, and restore it to `1` to resume. This pauses the watchdog; it does not cancel jobs already queued or running, disable query-time embeddings, or make `Enabled` and `Suspended` reload dynamically.

## Query

Search by supplying natural-language text as the value of the vector search parameter:

```http
GET /Observation?clinical-note=shortness%20of%20breath%20after%20exercise
```

Optional controls are:

* `_vectorK` bounds the nearest-neighbor result count, subject to `MaxK`.
* `_vectorMinScore` removes results below the requested normalized similarity score.

```http
GET /Observation?clinical-note=respiratory%20distress&_vectorK=20&_vectorMinScore=0.70
```

Matches are ordered by decreasing relevance. The normalized score is returned in `Bundle.entry.search.score` and is between zero and one.

Each matched entry also has a `semantic-search-evidence` extension on `Bundle.entry.search`:

```json
{
  "url": "http://microsoft.com/fhir/StructureDefinition/semantic-search-evidence",
  "extension": [
    { "url": "text", "valueString": "the matched source passage" },
    { "url": "chunkOrdinal", "valueInteger": 0 },
    { "url": "score", "valueDecimal": 0.82 }
  ]
}
```

The evidence contains stored source text. Apply the same access, logging, and disclosure controls to it as to the resource.

Normal FHIR restrictions still shape the candidate set. Compartment, authorization, and other search predicates are applied before relevance selection so a semantically closer resource outside the allowed set is not returned.

## Approximate vector index

`EnableApproximateVectorIndex` is a preview switch and is off by default. Enabling it does not create an index. A compatible approximate vector index must already exist on `dbo.VectorSearchParam`, and the server must detect it.

The approximate path is used only for a type-scoped vector search with no other restricting predicate. Searches with compartment, authorization, or additional search restrictions use the exact path so those restrictions are applied before nearest-neighbor selection. If the flag is enabled but the index is unavailable, the server logs the condition and uses exact search.

## Monitoring

The server emits these metrics:

| Metric | Meaning |
| --- | --- |
| `VectorSearch.EmbeddingCalls` | Embedding calls, tagged by `Operation` (`Read`, `Write`, or `BackgroundWrite`) and `Outcome` (`Success` or `Failure`). |
| `VectorSearch.EmbeddingLatency` | Embedding-call duration in milliseconds with the same tags. |
| `VectorSearch.BackgroundRowsWritten` | Vector rows written by background jobs. |
| `VectorSearch.BackgroundResourcesSkipped` | Resources skipped because their text hash and embedding model identity were unchanged. |
| `VectorSearch.Queries` | Vector queries, tagged with `Shape=Exact` or `Shape=Approximate`. |

Alert on embedding failures and sustained latency increases. Compare background rows written with resources skipped during normal watchdog sweeps. A healthy steady-state sweep should skip most unchanged resources. A near-zero `VectorSearch.BackgroundResourcesSkipped` count means something is re-embedding needlessly, commonly because model identity or chunk settings are changing, stored state is not being reused, or resources are being rewritten continuously.

Also monitor queue depth, watchdog progress, Azure AI Foundry throttling, SQL storage growth, and embedding cost. The first watchdog sweep after enabling vector search can be a large billable backfill.

## Limitations

* Only the SQL Server storage provider supports vector search.
* Schema V117 cannot run on SQL Server 2019 or SQL Server 2022.
* Search availability is eventually consistent in asynchronous mode.
* `$import` does not embed resources inline.
* Text and chunk caps can leave later document content unindexed.
* Approximate indexing is preview functionality and applies only to an unrestricted type-scoped query shape.
* Configuration changes to `Enabled` or `Suspended` require a process restart or rolling redeployment.
* Changing the model version or chunk settings does not automatically schedule a full historical re-embedding sweep.
