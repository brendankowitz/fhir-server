# Ignixa/Firely Provider Organization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Firely/Ignixa implementation pairs easy to find side-by-side, without a broad file-moving reorg that would create a permanent upstream-merge-conflict tax. Scope decided by an independent Fable architecture review (full design at the end of this plan's commit history — see the dispatch that produced it): reject a literal `*.Firely`/`*.Ignixa` sibling-project split as not worth the cost before US-24; execute four narrow, high-value slices instead.

**Architecture:** Organize by feature, discriminate by SDK prefix — not by SDK folder. Ignixa implementations live beside their Firely counterpart in the same feature folder, prefixed `Ignixa`; upstream-inherited Firely implementations keep their original (unprefixed) name and path to minimize merge conflicts against `microsoft/fhir-server` main; branch-new Firely implementations get a `Firely` prefix. A `provider-map.md` doc covers the seams no folder layout can express (inline mode branches, data-driven fallbacks).

**Tech Stack:** C#/.NET 9, shared-project MSBuild pattern (`.projitems`), xunit.

## Global Constraints

- **Every file moved/renamed in this plan is branch-new** (created earlier in this same effort, never existed on `main`) — this is what makes Slices 1 and part of 2a safe from upstream merge conflict. Do not extend any task in this plan to touch a file that exists on `microsoft/fhir-server` main (verify with `git log --oneline main -- <path>` if in doubt before renaming/moving anything not explicitly listed here).
- **Do not attempt a `*.Firely` sibling project or a broad `Firely\`/`Ignixa\` folder reorg.** This was assessed and explicitly rejected — see Architecture above. If a task in this plan seems to invite expanding scope in that direction, don't.
- This repo enforces StyleCop alphabetical `using` ordering as a build error (`TreatWarningsAsErrors`). Every new/moved `using` must be inserted in alphabetical order relative to its neighbors.
- The shared-project pattern requires every `.cs` file to be listed in the owning layer's `.projitems` file (`Shared.Core.projitems`, `Shared.Api.projitems`, or the corresponding `.UnitTests.projitems`) or it will not compile into any concrete project.
- `Microsoft.Health.Fhir.Ignixa` (the standalone project) references only `Microsoft.Health.Fhir.Core` — it cannot see `Microsoft.Health.Fhir.Api`/`Shared.Api` types. Types compiled via `Shared.Core.projitems` are `internal` to the versioned Core assemblies (`R4.Core`, etc.), and those assemblies' `InternalsVisibleTo` extends to `*.Api.UnitTests` but NOT the production `*.Api` assemblies.
- Build verification command for every task: `dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60` — must show `0 Warning(s)` beyond pre-existing ones you did not introduce. Pre-existing, unrelated failures you may see and must not try to fix: the four `Microsoft.Health.Fhir.*.Tests.E2E` SDK-version environment failures (`global.json requires 9.0.314`), and occasional transient Roslyn/MSBuild crashes (`AccessViolationException`, `Internal CLR error`, a corrupted `staticwebassets` cache JSON error) — if you hit one of these, retry the build once before reporting a concern; they have not reproduced on retry in this environment.

---

### Task 1: Delete dead/duplicate/orphaned Ignixa code (US-23)

**Files:**
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/FhirPath/IgnixaCompiledFhirPath.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/FhirPath/IgnixaFhirPathProvider.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IIgnixaJsonSerializer.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaJsonSerializer.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaResourceElement.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaResourceElementExtensions.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IIgnixaSchemaContext.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/FhirServerBuilderIgnixaPersistenceRegistrationExtensions.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/IIgnixaRawResourceFactory.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/IIgnixaResourceDeserializer.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/IgnixaRawResourceFactory.cs`
- Delete: `src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/IgnixaResourceDeserializer.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems`

**Interfaces:** None — every file in this task is confirmed uncompiled (the first 7) or compiled-but-zero-call-sites (the last 5). This task changes no runtime behavior.

**Why these 12 and not others:** confirmed this session (re-verified against a fresh diff of each of the 7 `Shared.Core/Ignixa/*` files against their live counterparts in `Microsoft.Health.Fhir.Ignixa`/elsewhere — 6 byte-identical, 1 drifted at `FhirPath/IgnixaCompiledFhirPath.cs`) and against the `.projitems` compile list (the 5 persistence-scaffolding files ARE compiled but `AddIgnixaPersistence` — their only entry point — has zero callers anywhere in `src/`).

- [ ] **Step 1: Confirm zero external references before deleting (safety check, not optional)**

Run, for each of the 12 files above, a repo-wide grep for its class/interface name (e.g. `grep -rn "IgnixaResourceElement\b" --include=*.cs src test`) and confirm every hit is either (a) inside the file being deleted, (b) inside another file also being deleted in this task, or (c) — for the 7 files under `Shared.Core/Ignixa/*` — a reference to the *identically-named* type in `Microsoft.Health.Fhir.Ignixa` (the standalone project; these are the duplicate class names, so a grep hit alone does not tell you which of the two definitions it resolves to — check the file's actual `using`/namespace to confirm it resolves to `Microsoft.Health.Fhir.Ignixa`, not `Microsoft.Health.Fhir.Ignixa` compiled via `Shared.Core.projitems`, which is NOT possible to distinguish by name alone since both use namespace `Microsoft.Health.Fhir.Ignixa` — instead confirm via the `.projitems` compile list from Global Constraints: only `IgnixaFhirJsonInputFormatter.cs`, `IgnixaFhirJsonOutputFormatter.cs`, and `ServiceCollectionExtensions.cs` under `Shared.Core/Ignixa/` are actually compiled; the 7 files in this task are not in that compile list, so nothing outside this task's own file set can be *compiling against* their definitions even if names collide).

If this check surfaces a surprise (a real external reference you can't explain), STOP and report BLOCKED rather than deleting — this is exactly the kind of thing worth a second look before an irreversible-feeling change (git history makes it reversible, but don't rely on that as a substitute for checking first).

- [ ] **Step 2: Delete the 12 files**

```bash
git rm src/Microsoft.Health.Fhir.Shared.Core/Ignixa/FhirPath/IgnixaCompiledFhirPath.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Ignixa/FhirPath/IgnixaFhirPathProvider.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IIgnixaJsonSerializer.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaJsonSerializer.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaResourceElement.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaResourceElementExtensions.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IIgnixaSchemaContext.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/FhirServerBuilderIgnixaPersistenceRegistrationExtensions.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/IIgnixaRawResourceFactory.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/IIgnixaResourceDeserializer.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/IgnixaRawResourceFactory.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/IgnixaResourceDeserializer.cs
```

If the now-empty `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/FhirPath/` directory remains on disk after `git rm` (git doesn't track empty directories), that's expected — leave it; it will disappear naturally or can be removed with `rmdir` if it bothers you, but don't spend a step on it.

- [ ] **Step 3: Remove the corresponding `<Compile Include>` entries from `Shared.Core.projitems`**

Read the file first to find the exact current lines (they were confirmed present at approximately lines 14-16 and 48-53 as of this plan's writing, but re-verify — do not assume line numbers). Remove these 12 lines (do not touch any other `Ignixa`-related entries — `IgnixaSchemaContext.cs`, `Features/Validation/IgnixaResourceValidator.cs`, `Extensions/ResourceElementIgnixaExtensions.cs` are LIVE and must stay):

```xml
<Compile Include="$(MSBuildThisFileDirectory)Ignixa\FhirPath\IgnixaCompiledFhirPath.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Ignixa\FhirPath\IgnixaFhirPathProvider.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Ignixa\IIgnixaJsonSerializer.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Ignixa\IgnixaJsonSerializer.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Ignixa\IgnixaResourceElement.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Ignixa\IgnixaResourceElementExtensions.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Ignixa\IIgnixaSchemaContext.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Features\Persistence\FhirServerBuilderIgnixaPersistenceRegistrationExtensions.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Features\Persistence\IIgnixaRawResourceFactory.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Features\Persistence\IIgnixaResourceDeserializer.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Features\Persistence\IgnixaRawResourceFactory.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Features\Persistence\IgnixaResourceDeserializer.cs" />
```

Note: `Features\Persistence\IgnixaSchemaContext.cs` (no `I` prefix — a different, LIVE file, registered at `FhirModule.cs:112`) sits adjacent to the deleted persistence entries — be precise, do not delete its line.

- [ ] **Step 4: Build and run tests**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Ignixa.UnitTests/Microsoft.Health.Fhir.Ignixa.UnitTests.csproj --no-restore
```

Expected: clean build (aside from the pre-existing failures in Global Constraints), all tests pass with the same counts as before this task (this task deletes only dead code — zero test count change expected).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "US-23: Delete dead/duplicate/orphaned Ignixa code

Removes 7 uncompiled duplicate files under Shared.Core/Ignixa/ (6
byte-identical to their live counterparts in Microsoft.Health.Fhir.Ignixa,
1 already drifted) and 5 compiled-but-zero-callers persistence scaffolding
files (AddIgnixaPersistence and its IIgnixaRawResourceFactory/
IIgnixaResourceDeserializer dependents). No runtime behavior change."
```

---

### Task 2: Swap import-parser naming to match the SDK-prefix convention

**Files:**
- Rename: `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/ImportResourceParser.cs` → `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/IgnixaImportResourceParser.cs`
- Rename: `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/FirelyImportResourceParser.cs` → `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/ImportResourceParser.cs`
- Rename: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Operations/Import/ImportResourceParserTests.cs` → `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Operations/Import/IgnixaImportResourceParserTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/OperationsModule.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/OperationsModuleTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Microsoft.Health.Fhir.Shared.Core.UnitTests.projitems`

**Interfaces:** No change to `IImportResourceParser` (`src/Microsoft.Health.Fhir.Core/Features/Operations/Import/IImportResourceParser.cs`) — only the two implementing classes' names swap.

**Why:** confirmed by a repo-wide grep — exactly these 5 files reference `ImportResourceParser`/`FirelyImportResourceParser` (verified exhaustive; if your own grep during this task finds a 6th, STOP and report NEEDS_CONTEXT rather than silently expanding scope). Today the Ignixa implementation holds the generic name (`ImportResourceParser`) and the Firely one is prefixed (`FirelyImportResourceParser`) — backwards relative to every other pair in the codebase (`ModelAttributeValidator`/`IgnixaResourceValidator`, `FirelyFhirPathProvider`/`IgnixaFhirPathProvider`). Swapping restores the convention AND — because `FirelyImportResourceParser.cs`'s content is a near-byte-identical restore of `microsoft/fhir-server` main's original `ImportResourceParser.cs` (verified: it was transcribed from `git show 5b0a390df^:.../ImportResourceParser.cs`, the commit immediately before the Ignixa rewrite) — renaming it back to `ImportResourceParser.cs` makes this file textually match what's on main again, which *shrinks* rather than grows the eventual upstream merge diff for this file. This is the rare case where better naming and smaller merge risk point the same direction.

- [ ] **Step 1: Rename the Ignixa implementation to `IgnixaImportResourceParser`**

Read the current `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/ImportResourceParser.cs` in full first. Its current class declaration is `public class ImportResourceParser : IImportResourceParser` (namespace `Microsoft.Health.Fhir.Core.Features.Operations.Import`, unchanged). Use `git mv` to preserve history, then edit the class name:

```bash
git mv src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/ImportResourceParser.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/IgnixaImportResourceParser.cs
```

Edit the moved file: change `public class ImportResourceParser : IImportResourceParser` to `public class IgnixaImportResourceParser : IImportResourceParser`. Update the class's XML-doc summary (if it has one) to reflect the new name if the doc comment repeats the type name; do not otherwise change any logic in this file — this task is a pure rename, not a refactor.

- [ ] **Step 2: Rename the Firely implementation to `ImportResourceParser`**

Read the current `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/FirelyImportResourceParser.cs` in full first. Its current class declaration is `public class FirelyImportResourceParser : IImportResourceParser`.

```bash
git mv src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/FirelyImportResourceParser.cs \
       src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/ImportResourceParser.cs
```

Edit the moved file: change `public class FirelyImportResourceParser : IImportResourceParser` to `public class ImportResourceParser : IImportResourceParser`. Update its XML-doc summary similarly (it currently documents itself as "Firely-based `IImportResourceParser` selected by `FhirSdkMode.Firely`" or similar — keep that explanatory sentence, since it's genuinely useful context distinguishing it from its Ignixa sibling now living under a different name in the same folder; just remove the word "Firely" from the class-name reference in the doc comment if the comment names the type directly, since the type is no longer literally named that).

- [ ] **Step 3: Update `OperationsModule.cs`'s mode-gated registration**

Read the current file first (it should show a ctor and `Load` with an `if (_sdkMode == FhirSdkMode.Firely) { services.Add<FirelyImportResourceParser>()... } else { services.Add<ImportResourceParser>()... }` block, following US-6's implementation). Swap the two class names so the branches now read:

```csharp
if (_sdkMode == FhirSdkMode.Firely)
{
    services.Add<ImportResourceParser>()
        .Transient()
        .AsSelf()
        .AsService<IImportResourceParser>();
}
else
{
    services.Add<IgnixaImportResourceParser>()
        .Transient()
        .AsSelf()
        .AsService<IImportResourceParser>();
}
```

No `using` changes needed — both classes stay in the same namespace (`Microsoft.Health.Fhir.Core.Features.Operations.Import`, imported today as `Microsoft.Health.Fhir.Shared.Core.Features.Operations.Import` per the existing `using` in this file — verify which spelling the current file actually uses and keep it unchanged).

- [ ] **Step 4: Update `OperationsModuleTests.cs`'s assertions**

Read the current file first (it should have `GivenFirelyMode_WhenLoaded_ThenFirelyImportResourceParserIsRegistered` asserting `Assert.IsType<FirelyImportResourceParser>(parser)`, and a Hybrid/Ignixa theory test asserting `Assert.IsType<ImportResourceParser>(parser)`). Swap the asserted types to match Step 3:

```csharp
[Fact]
public void GivenFirelyMode_WhenLoaded_ThenImportResourceParserIsRegistered()
{
    var provider = BuildProvider(FhirSdkMode.Firely);

    var parser = provider.GetRequiredService<IImportResourceParser>();

    Assert.IsType<ImportResourceParser>(parser);
}

[Theory]
[InlineData(FhirSdkMode.Hybrid)]
[InlineData(FhirSdkMode.Ignixa)]
public void GivenHybridOrIgnixaMode_WhenLoaded_ThenIgnixaImportResourceParserIsRegistered(FhirSdkMode mode)
{
    var provider = BuildProvider(mode);

    var parser = provider.GetRequiredService<IImportResourceParser>();

    Assert.IsType<IgnixaImportResourceParser>(parser);
}
```

Rename the test method names too (shown above) so they describe what they now assert — don't leave a method named `...FirelyImportResourceParserIsRegistered` asserting `ImportResourceParser`. Leave the rest of the file (the null-ctor-guard test, `BuildProvider` helper) unchanged.

- [ ] **Step 5: Rename and update the dedicated parser test file**

```bash
git mv src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Operations/Import/ImportResourceParserTests.cs \
       src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Operations/Import/IgnixaImportResourceParserTests.cs
```

Read the moved file in full. It constructs and tests `ImportResourceParser` (the Ignixa implementation) — update every reference (the class name in the constructor call, the test class name if it's named e.g. `ImportResourceParserTests` → `IgnixaImportResourceParserTests`, matching xunit convention of test-class-name-matches-file-name) to `IgnixaImportResourceParser`. Do not change any test logic/assertions — this is a rename-only pass; the Ignixa implementation's behavior is unchanged.

- [ ] **Step 6: Update both `.projitems` files**

In `src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems`, find the two entries (confirmed at approximately these lines as of this plan's writing — re-verify):

```xml
<Compile Include="$(MSBuildThisFileDirectory)Features\Operations\Import\FirelyImportResourceParser.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Features\Operations\Import\ImportResourceParser.cs" />
```

Replace with (note the alphabetical order flips too — `IgnixaImportResourceParser` sorts after `ImportResourceParser` now: "Ignixa" > "Import" at the 3rd character, `g` > `p`... actually check carefully: "IgnixaImportResourceParser" vs "ImportResourceParser" — compare char by char: `I`-`g` vs `I`-`m`; `g` &lt; `m`, so `IgnixaImportResourceParser` sorts BEFORE `ImportResourceParser`):

```xml
<Compile Include="$(MSBuildThisFileDirectory)Features\Operations\Import\IgnixaImportResourceParser.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Features\Operations\Import\ImportResourceParser.cs" />
```

(Same two-line result, order unchanged as it happens — `IgnixaImportResourceParser` still sorts first. Just confirm the filenames in each line match the renamed files.)

In `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Microsoft.Health.Fhir.Shared.Core.UnitTests.projitems`, find and update:

```xml
<Compile Include="$(MSBuildThisFileDirectory)Features\Operations\Import\ImportResourceParserTests.cs" />
```

→

```xml
<Compile Include="$(MSBuildThisFileDirectory)Features\Operations\Import\IgnixaImportResourceParserTests.cs" />
```

- [ ] **Step 7: Build and run tests**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

Expected: clean build, all tests pass with the same counts as before (pure rename, zero behavior change — the renamed `IgnixaImportResourceParserTests` tests should pass identically to how `ImportResourceParserTests` passed before the rename).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Reorg: swap import-parser naming to match SDK-prefix convention

ImportResourceParser (the Ignixa implementation) -> IgnixaImportResourceParser.
FirelyImportResourceParser -> ImportResourceParser, restoring the generic
name to the Firely implementation, matching every other Firely/Ignixa pair
in the codebase (ModelAttributeValidator/IgnixaResourceValidator,
FirelyFhirPathProvider/IgnixaFhirPathProvider). Also shrinks the upstream
merge diff for this file back toward zero, since its content already
matches main's pre-Ignixa-rewrite version. Pure rename, no behavior change."
```

---

### Task 3: Add the provider map

**Files:**
- Create: `docs/features/sdk-migration/provider-map.md`
- Modify: `docs/features/sdk-migration/readme.md`

**Interfaces:** None — documentation only.

- [ ] **Step 1: Write the provider map**

Create `docs/features/sdk-migration/provider-map.md`:

```markdown
# Firely/Ignixa Provider Map

One row per mode-gated seam: which implementation serves `SdkMode.Firely`, which
serves `SdkMode.Ignixa`/`Hybrid`, and where the selection happens. Keep this
current as new seams gain per-mode implementations (each new US-1x/2x story
that adds one should add a row here as part of its done-criteria).

| Seam | Firely implementation | Ignixa implementation | Selected by |
|---|---|---|---|
| JSON input formatter | `Shared.Api/Features/Formatters/FhirJsonInputFormatter.cs` | `Shared.Api/Features/Formatters/IgnixaFhirJsonInputFormatter.cs` | `Shared.Api/Modules/FeatureFlags/SdkMode/SdkModeFeatureModule.cs` |
| JSON output formatter | `Shared.Api/Features/Formatters/FhirJsonOutputFormatter.cs` | `Shared.Api/Features/Formatters/IgnixaFhirJsonOutputFormatter.cs` | same |
| FHIRPath provider | `Core/Features/Search/FhirPath/FirelyFhirPathProvider.cs` + `FirelyCompiledFhirPath.cs` | `Microsoft.Health.Fhir.Ignixa/FhirPath/IgnixaFhirPathProvider.cs` + `IgnixaCompiledFhirPath.cs` | `Shared.Api/Modules/FhirModule.cs` (skips `AddIgnixaFhirPath` in Firely mode) + `Shared.Api/Modules/SearchModule.cs` (`TryAddSingleton` fallback) |
| Structural validation | `Shared.Core/Features/Validation/ModelAttributeValidator.cs` | `Shared.Core/Features/Validation/IgnixaResourceValidator.cs` (wraps the Firely fallback) | `Shared.Api/Modules/ValidationModule.cs` |
| `$import` parsing | `Shared.Core/Features/Operations/Import/ImportResourceParser.cs` | `Shared.Core/Features/Operations/Import/IgnixaImportResourceParser.cs` | `Shared.Api/Modules/OperationsModule.cs` |
| DB-read JSON deserialization | inline Firely branch | inline Ignixa branch | inline `_sdkMode` check, `Shared.Api/Modules/FhirModule.cs` (JSON deserializer dictionary factory) |

## SDK-neutral by design (no pairing, and none expected)

These are data-driven — the same class handles both SDKs at runtime based on
whether the resource in hand actually carries an Ignixa node
(`GetIgnixaNode() != null`), with no `SdkMode` check and no separate
implementation class. Do not create a Firely/Ignixa pair for these; per
ADR-2607 this is the intended long-term shape once every write/read path is
node-aware.

- `Shared.Core/Features/Persistence/RawResourceFactory.cs`
- `Shared.Core/Features/Resources/Bundle/BundleSerializer.cs`
- `Shared.Core/Features/Resources/Create/CreateResourceHandler.cs`, `Shared.Core/Features/Resources/Upsert/UpsertResourceHandler.cs` (partial — see `user-stories.md` US-12)

## Deliberately unpaired (deferred, named exceptions per objective 5)

| Seam | Why no Ignixa side exists | Story |
|---|---|---|
| XML formatters | No upstream Ignixa XML support at all | US-E3 |
| Terminology | Upstream is membership-only; `$expand`/`$lookup` stubbed | US-E5 |
| FHIRPath/JSON Patch | Blocked on upstream `FhirPatchEngine` packaging | US-E2 |
| Anonymized export | `Microsoft.Health.Fhir.Anonymizer.*` packages are Firely-POCO-based | US-E6 |

## Planned pairs (Firely side exists today; Ignixa side is future work)

| Story | Seam | Firely side today | Planned Ignixa side |
|---|---|---|---|
| US-15 | Search/history bundle assembly | `Shared.Core/Features/Search/BundleFactory.cs` | `IgnixaBundleFactory` (planned name — confirm at implementation time) |
| US-16 | Batch/transaction | `Shared.Api/Features/Resources/Bundle/BundleHandler.cs` (+ 4 versioned partials) | new Ignixa bundle processing |
| US-20 | Profile validation | `Shared.Core/Features/Validation/ProfileValidator.cs` | `IgnixaProfileValidator` (planned name) |
```

- [ ] **Step 2: Link it from the feature readme**

Read `docs/features/sdk-migration/readme.md` first. Add a line near the top (after the Problem Statement / near the Investigations table — pick whichever existing section makes it most discoverable, following the file's current structure) pointing to the new doc, e.g.:

```markdown
See [provider-map.md](provider-map.md) for the current Firely/Ignixa implementation-pair inventory.
```

- [ ] **Step 3: Commit**

```bash
git add docs/features/sdk-migration/provider-map.md docs/features/sdk-migration/readme.md
git commit -m "Add Firely/Ignixa provider map

One-page index of every mode-gated seam's Firely/Ignixa implementation
pair, the SDK-neutral data-driven components that intentionally have no
pair, and the deliberately-unpaired deferred seams. Maintained going
forward as new seams gain per-mode implementations."
```

---

### Task 4: Relocate the Ignixa JSON formatters beside their Firely counterparts

**Files:**
- Move: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaFhirJsonInputFormatter.cs` → `src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/IgnixaFhirJsonInputFormatter.cs`
- Move: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaFhirJsonOutputFormatter.cs` → `src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/IgnixaFhirJsonOutputFormatter.cs`
- Move: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Ignixa/IgnixaFhirJsonInputFormatterTests.cs` → `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Features/Formatters/IgnixaFhirJsonInputFormatterTests.cs`
- Move: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Ignixa/IgnixaFhirJsonOutputFormatterTests.cs` → `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Features/Formatters/IgnixaFhirJsonOutputFormatterTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/ServiceCollectionExtensions.cs` (split — formatter registration moves out)
- Create or modify: a Shared.Api-layer home for the formatter DI registration (see Step 3)
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/FeatureFlags/SdkMode/SdkModeFeatureModule.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Microsoft.Health.Fhir.Shared.Api.projitems`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Microsoft.Health.Fhir.Shared.Core.UnitTests.projitems`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems`

**Interfaces:**
- Produces: `IgnixaFhirJsonInputFormatter`/`IgnixaFhirJsonOutputFormatter` become `internal sealed` classes compiled into the *Api* layer (same assemblies as `FhirJsonInputFormatter`/`FhirJsonOutputFormatter`, `SdkModeFeatureModule`, `FormatterConfiguration`) instead of the Core layer. `SdkModeFeatureModule.RegisterIgnixaJsonFormatters` can then register them directly (`services.Add<IgnixaFhirJsonInputFormatter>().Singleton().AsSelf().AsService<TextInputFormatter>()`, mirroring `RegisterFirelyJsonFormatters` exactly) instead of calling the `AddIgnixaMvcFormatters()` forwarder, which this task deletes.

**This is the one task in this plan that changes a real architectural boundary (Core → Api), not just a file location — read this whole task before starting any step, and re-verify every claim below against the current files rather than trusting this summary, since it's more involved than Tasks 1-3.**

**Why this is safe:** confirmed this session — the two formatters' only dependencies are: Ignixa SDK types (`Ignixa.Abstractions`, `Ignixa.Serialization.SourceNodes` — available anywhere), Firely SDK types (`Hl7.Fhir.*` — available in Api), `IIgnixaJsonSerializer`/`IgnixaResourceElement`/`IIgnixaSchemaContext` (public types in the standalone `Microsoft.Health.Fhir.Ignixa` project, which `Shared.Api.projitems` already references), `BundleSerializer`/`RawBundleEntryComponent` (public types in `Shared.Core`, visible from Api), `ResourceDeserializer` (public, `Shared.Core`), and the internal two-arg `ResourceElement` constructor (defined in `Microsoft.Health.Fhir.Core`, whose `InternalsVisibleTo` list already includes `Microsoft.Health.Fhir.Api` and all four versioned Api assemblies). Every dependency is visible from the Api layer today — nothing new needs to become public, and this move actually *removes* the last `Microsoft.AspNetCore.Mvc` references from the Core layer (a pre-existing, accidental layering wart).

- [ ] **Step 1: Move the two formatter files**

Read both files in full first (`src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaFhirJsonInputFormatter.cs`, `.../IgnixaFhirJsonOutputFormatter.cs`).

```bash
git mv src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaFhirJsonInputFormatter.cs \
       src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/IgnixaFhirJsonInputFormatter.cs
git mv src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaFhirJsonOutputFormatter.cs \
       src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/IgnixaFhirJsonOutputFormatter.cs
```

**Keep the namespace unchanged: `Microsoft.Health.Fhir.Ignixa`** (do NOT change it to `Microsoft.Health.Fhir.Api.Features.Formatters` — the codebase already tolerates namespace/folder mismatches driven by shared-project `Import_RootNamespace` conventions, e.g. `FirelyTerminologyServiceProxy` lives under a `Shared.Core` folder but sits in namespace `Microsoft.Health.Fhir.Shared.Core.Features.Conformance`. Keeping the `Microsoft.Health.Fhir.Ignixa` namespace here means every one of the ~40+ files across `src/` and `test/` that already `using Microsoft.Health.Fhir.Ignixa;` to reference `IgnixaResourceElement`/`IIgnixaJsonSerializer`/etc. needs zero changes, and this task's own blast radius stays limited to the files listed above). Both files' `namespace Microsoft.Health.Fhir.Ignixa;` line stays exactly as-is after the move — verify this is still true after the `git mv`, since the move itself doesn't touch file content.

No other content in either file changes in this step — no `using` changes yet (Step 2 handles that if needed).

- [ ] **Step 2: Verify/fix each moved file's usings still resolve after the layer change**

Both files already `using Microsoft.AspNetCore.Mvc.Formatters;`, `Microsoft.Health.Fhir.Core.*`, `Ignixa.*`, `Hl7.Fhir.*` — all of these remain valid from the Api layer (Api references Core; the packages are referenced solution-wide via central package management). Re-read each file's current `using` block after the move and confirm no line references a Core-layer-only type that doesn't exist from Api's perspective — based on this session's research, there should be none, but this step exists to catch anything the research missed. If you find a genuine problem here, it very likely means one of this task's "why this is safe" claims above was wrong for a specific type; STOP and report BLOCKED with the specific type and error rather than working around it.

- [ ] **Step 3: Split `ServiceCollectionExtensions.cs` — move formatter-specific registration out of the standalone Ignixa layer**

Read the current `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/ServiceCollectionExtensions.cs` in full (namespace `Microsoft.Health.Fhir.Ignixa`, static class `ServiceCollectionExtensions`, currently has four methods: `AddIgnixaSerialization`, `AddIgnixaMvcFormatters`, `AddIgnixaFhirPath`).

- `AddIgnixaSerialization` (registers `IIgnixaJsonSerializer` and the two concrete formatter singletons) and `AddIgnixaFhirPath` (registers the FHIRPath provider) have **no Api/MVC dependency** beyond the formatter singleton registrations inside `AddIgnixaSerialization` — **leave both of these methods in this file, in this location** (`Shared.Core/Ignixa/ServiceCollectionExtensions.cs`), since they're consumed by `FhirModule.cs` (a Shared.Api file, but one that already references Core-layer registration methods — this is normal, not a new pattern) and this split isn't about moving every Ignixa registration, only the ones that exist purely to work around the internal-visibility problem this task is eliminating.
- **Delete `AddIgnixaMvcFormatters` entirely** (its whole reason to exist — bridging an internal-visibility gap — no longer applies once the formatters are `internal` to the *same* assembly as their caller). Its doc comment already states this reasoning; re-read it before deleting so you understand exactly what's being removed and why.
- The two lines inside `AddIgnixaSerialization` that register the formatters as `TextInputFormatter`/`TextOutputFormatter` services stay — but re-examine them: today `AddIgnixaSerialization` registers the formatters `AsSelf` only (concrete singletons), and `AddIgnixaMvcFormatters` (being deleted) is what exposed them `AsService<TextInputFormatter>`. Since `SdkModeFeatureModule` will now register them directly (Step 4), **`AddIgnixaSerialization` should keep registering the two formatters as concrete singletons (`AsSelf`) exactly as it does today — do not add `TextInputFormatter`/`TextOutputFormatter` service registrations here**; that responsibility moves entirely to `SdkModeFeatureModule` (mirroring exactly how `RegisterFirelyJsonFormatters` already does it with `.AsSelf().AsService<TextInputFormatter>()` via the fluent builder, a single registration call rather than two separate ones).

After this step, `ServiceCollectionExtensions.cs` should have three methods (`AddIgnixaSerialization`, `AddIgnixaFhirPath`, and no `AddIgnixaMvcFormatters`), and its `using Microsoft.AspNetCore.Mvc.Formatters;` line (needed today for `TextInputFormatter`/`TextOutputFormatter` in `AddIgnixaMvcFormatters`'s signature) may become unused — check and remove it if so (verify no other line in the file still needs it; `AddIgnixaSerialization`'s formatter registrations reference the concrete formatter types, not `TextInputFormatter`/`TextOutputFormatter`, so this using is very likely now dead).

- [ ] **Step 4: Update `SdkModeFeatureModule.cs` to register the Ignixa formatters directly**

Read the current file in full (shown in this plan's research as of its writing — re-verify). Replace:

```csharp
private static void RegisterIgnixaJsonFormatters(IServiceCollection services)
{
    services.AddIgnixaMvcFormatters();
}
```

with:

```csharp
private static void RegisterIgnixaJsonFormatters(IServiceCollection services)
{
    services.Add<IgnixaFhirJsonInputFormatter>()
        .Singleton()
        .AsSelf()
        .AsService<TextInputFormatter>();

    services.Add<IgnixaFhirJsonOutputFormatter>()
        .Singleton()
        .AsSelf()
        .AsService<TextOutputFormatter>();
}
```

This mirrors `RegisterFirelyJsonFormatters` exactly (same fluent builder shape) — the whole point of this task. **Important:** `IgnixaFhirJsonInputFormatter`'s constructor takes `(IIgnixaJsonSerializer serializer, FhirJsonParser firelyParser, IServiceProvider serviceProvider)` — a plain `services.Add<IgnixaFhirJsonInputFormatter>()` (no factory lambda) requires all three to be independently resolvable from the container, which they are (`IIgnixaJsonSerializer` via `AddIgnixaSerialization`, `FhirJsonParser` via `FhirModule`'s singleton registration, `IServiceProvider` is always resolvable) — confirm this works when you build/test (Step 6); if DI can't construct it this way, the fallback is to keep a small factory-based registration here instead of the plain `.Add<T>()` form, but attempt the direct form first since it's simpler and matches the Firely side's shape.

`SdkModeFeatureModule.cs`'s `using Microsoft.Health.Fhir.Ignixa;` (already present, for the `AddIgnixaMvcFormatters` extension method) now needs to resolve `IgnixaFhirJsonInputFormatter`/`IgnixaFhirJsonOutputFormatter` by name instead — since the namespace didn't change (Step 1), this using still resolves correctly; no using changes needed in this file.

- [ ] **Step 5: Update `FhirModule.cs` if it references the deleted `AddIgnixaMvcFormatters` anywhere**

Grep the current `FhirModule.cs` for `AddIgnixaMvcFormatters` and `AddIgnixaSerialization`. Per this session's prior work, `FhirModule.cs` should already call only `services.AddIgnixaSerialization();` (unconditionally, for the serializer) with formatter-family selection fully delegated to `SdkModeFeatureModule` — if that's what you find, no change is needed here. If you find any other reference to the deleted method, update it to use the pattern from Step 4 instead, and note this deviation from the expected state in your report.

- [ ] **Step 6: Update all four `.projitems` files**

`src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems` — remove:
```xml
<Compile Include="$(MSBuildThisFileDirectory)Ignixa\IgnixaFhirJsonInputFormatter.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Ignixa\IgnixaFhirJsonOutputFormatter.cs" />
```
(keep the `Ignixa\ServiceCollectionExtensions.cs` entry — that file stays in Shared.Core per Step 3).

`src/Microsoft.Health.Fhir.Shared.Api/Microsoft.Health.Fhir.Shared.Api.projitems` — add, in alphabetical order among the existing `Features\Formatters\*.cs` entries (this folder should already list `FhirJsonInputFormatter.cs`, `FhirJsonOutputFormatter.cs`, etc. — read the file to find the exact insertion points):
```xml
<Compile Include="$(MSBuildThisFileDirectory)Features\Formatters\IgnixaFhirJsonInputFormatter.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Features\Formatters\IgnixaFhirJsonOutputFormatter.cs" />
```

`src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Microsoft.Health.Fhir.Shared.Core.UnitTests.projitems` — remove the two `Ignixa\IgnixaFhirJson{Input,Output}FormatterTests.cs` entries.

`src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems` — add, alphabetically among `Features\Formatters\*.cs` test entries:
```xml
<Compile Include="$(MSBuildThisFileDirectory)Features\Formatters\IgnixaFhirJsonInputFormatterTests.cs" />
<Compile Include="$(MSBuildThisFileDirectory)Features\Formatters\IgnixaFhirJsonOutputFormatterTests.cs" />
```

- [ ] **Step 7: Move the two test files and fix their namespaces**

```bash
git mv src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Ignixa/IgnixaFhirJsonInputFormatterTests.cs \
       src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Features/Formatters/IgnixaFhirJsonInputFormatterTests.cs
git mv src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Ignixa/IgnixaFhirJsonOutputFormatterTests.cs \
       src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Features/Formatters/IgnixaFhirJsonOutputFormatterTests.cs
```

Read each moved file in full. Their current namespace is `Microsoft.Health.Fhir.Core.UnitTests.Ignixa` (confirmed for the input-formatter test; verify the output-formatter test's exact current namespace, it may differ slightly). Since these now physically live under `Shared.Api.UnitTests`, change the namespace to match that project's convention — check a sibling file already in `Shared.Api.UnitTests/Features/Formatters/` (e.g. `FhirJsonOutputFormatterTests.cs`, referenced elsewhere in this session's work as living in namespace `Microsoft.Health.Fhir.Api.UnitTests.Features.Formatters`) and match it exactly: `Microsoft.Health.Fhir.Api.UnitTests.Features.Formatters`.

Do not change any test logic. Check each file's `using` block for anything that was only valid from the old Core-layer test project location (unlikely, since both already `using Microsoft.Health.Fhir.Ignixa;` and Firely/Ignixa types by their public names) — fix in alphabetical order if anything needs adding (e.g. if the sibling `FhirJsonOutputFormatterTests.cs` pattern requires a using this file didn't previously need).

- [ ] **Step 8: Build and run tests**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Ignixa.UnitTests/Microsoft.Health.Fhir.Ignixa.UnitTests.csproj --no-restore
```

Expected: clean build. The two moved test files' assertions should pass identically to before the move (pure relocation) — verify their test counts show up in the `R4.Api.UnitTests` run now (they moved out of `R4.Core.UnitTests`) and confirm `R4.Core.UnitTests`'s total count dropped by exactly the number of tests in those two files, with `R4.Api.UnitTests`'s count rising by the same amount. Also re-run `SdkModeFeatureModuleTests` and `FormatterConfigurationTests` (both already inside the `R4.Api.UnitTests` run) — these are the order-assertion tests most likely to catch a real regression if the formatter registration change in Step 4 behaves differently than the old `AddIgnixaMvcFormatters` path; confirm they still pass with the same mode-based assertions as before (Ignixa-only / Firely-only / Hybrid-both-Ignixa-first).

If anything fails here, this is the task most likely in this whole plan to need real debugging (it's the one genuine architectural-boundary change) — work through it methodically rather than reaching for a workaround; if you can't resolve it within a reasonable number of attempts, report BLOCKED with exactly what you tried.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "Reorg: relocate Ignixa JSON formatters beside their Firely counterparts

Moves IgnixaFhirJsonInputFormatter/OutputFormatter from Shared.Core/Ignixa/
to Shared.Api/Features/Formatters/, alongside FhirJsonInputFormatter/
OutputFormatter -- the highest-traffic seam in the Firely/Ignixa provider
map, now visible side-by-side in one folder. This also deletes the
AddIgnixaMvcFormatters() forwarder: it existed solely to bridge an
internal-visibility gap (the formatters were internal to the Core-layer
assemblies, whose InternalsVisibleTo doesn't extend to production Api
assemblies) that no longer applies once the formatters compile into the
same Api-layer assembly as SdkModeFeatureModule, which now registers them
directly, mirroring the Firely registration exactly. Also removes the last
Microsoft.AspNetCore.Mvc references from the Core layer.

Namespace kept as Microsoft.Health.Fhir.Ignixa (unchanged) so the ~40+
existing consumers of that namespace across src/ and test/ need zero
changes -- this repo already tolerates folder/namespace mismatches driven
by the shared-project pattern (e.g. FirelyTerminologyServiceProxy)."
```

---

## After All Tasks

The Ignixa/Firely provider organization goal is satisfied to the extent Fable's design review recommended: dead code is gone (US-23), the one clearly-inverted naming pair is fixed, the highest-traffic seam (JSON formatters) is colocated with its counterpart, and a maintained index (`provider-map.md`) covers everything a folder structure can't express. A broad `*.Firely` project or folder-wide reorg was explicitly assessed and rejected as not worth the upstream-merge-conflict cost before US-24 — do not revisit that decision without a new architecture review, since the tradeoffs (blast radius vs. discoverability gain) haven't changed. New seams gained by future stories (US-15's `IgnixaBundleFactory`, US-20's `IgnixaProfileValidator`, etc.) should follow the convention established here (colocate, prefix, add a provider-map row) without needing their own reorg task.
