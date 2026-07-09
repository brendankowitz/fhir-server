# Force-Firely Phase 2 (US-6, US-7, US-8) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `FhirSdkMode.Firely` (added in US-1) actually produce a pure-Firely runtime by restoring/selecting Firely implementations for the three remaining unconditional Ignixa seams: the `$import` resource parser, the DB-read JSON deserializer, and the FHIRPath provider / model validator selection.

**Architecture:** Follow the `Modules/FeatureFlags/*FeatureModule` / module-constructor pattern already established by `SdkModeFeatureModule` (US-1) and the pre-existing `SecurityModule`/`XmlFormatterFeatureModule`: give the module that owns each registration a `FhirServerConfiguration` constructor, read `.CoreFeatures.SdkMode` once, and branch registration on it. No new abstractions — this is mode-gating existing registration call sites, one per story, each independently testable by instantiating the module directly (not via `RegisterAssemblyModules`) and asserting on `IServiceCollection`/`IServiceProvider` contents, exactly as `SdkModeFeatureModuleTests.cs` already does.

**Tech Stack:** C#/.NET 9, ASP.NET Core DI (`Microsoft.Extensions.DependencyInjection`), `Microsoft.Health.Extensions.DependencyInjection` fluent registration helpers, xunit.

## Global Constraints

- `FhirSdkMode` values are `Hybrid = 0` (default), `Firely = 1`, `Ignixa = 2` — defined in `src/Microsoft.Health.Fhir.Core/Configs/FhirSdkMode.cs` (already exists, US-1).
- `CoreFeatureConfiguration.SdkMode` (`src/Microsoft.Health.Fhir.Core/Configs/CoreFeatureConfiguration.cs:41`) is the single source of truth for the mode; every task reads it via `fhirServerConfiguration.CoreFeatures.SdkMode` in a module constructor, matching `SdkModeFeatureModule`'s existing shape — do not introduce a second way to read the mode.
- Every gated registration must still behave correctly in `Hybrid` mode exactly as it does today (Hybrid is not "no-op" — it is "keep existing Ignixa-preferred behavior unchanged"). Only `Firely` mode changes behavior in this plan.
- Do not touch `IIgnixaSchemaContext`'s registration (`FhirModule.cs`, `services.AddSingleton<IIgnixaSchemaContext, IgnixaSchemaContext>();`) — it must stay unconditionally registered in all three modes. `AddSingleton` is lazy (constructed on first resolve), and `IgnixaSchemaContext`'s constructor only throws for FHIR versions this server doesn't ship, so leaving it registered-but-unconstructed in Firely mode is safe and is the intended design (per ADR-2607 / US-8's own description) — do not add a mode branch around this line in any task.
- This repo enforces StyleCop alphabetical `using` ordering as a build error (`TreatWarningsAsErrors`). Every new `using` must be inserted in alphabetical order relative to its neighbors — check the surrounding lines before inserting, do not append to the end of the block.
- Follow the shared-project pattern already used throughout this codebase: a `.cs` file physically under `Microsoft.Health.Fhir.Shared.Core/` or `Microsoft.Health.Fhir.Shared.Api/` must have its `<Compile Include>` added to that folder's `.projitems` file, or it will not compile into any concrete project.
- Build verification command for every task: `dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60` — must show `0 Warning(s)` beyond pre-existing ones you did not introduce, and the only acceptable errors are the four pre-existing `Microsoft.Health.Fhir.*.Tests.E2E` SDK-version environment failures (`global.json requires 9.0.314`) which are unrelated to this work and already present on `main` of this branch — do not attempt to fix those.

---

### Task 1: US-6 — Restore the Firely `$import` parser behind `SdkMode`

**Files:**
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/FirelyImportResourceParser.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/OperationsModule.cs`
- Test: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/OperationsModuleTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems`

**Interfaces:**
- Consumes: `Microsoft.Health.Fhir.Core.Configs.FhirSdkMode` (enum, `Hybrid`/`Firely`/`Ignixa`), `Microsoft.Health.Fhir.Api.Configs.FhirServerConfiguration` (has settable-through `.CoreFeatures.SdkMode`), `Microsoft.Health.Fhir.Core.Features.Operations.Import.IImportResourceParser` (existing interface, one method: `ImportResource Parse(long index, long offset, int length, string rawResource, ImportMode importMode)`), `Microsoft.Health.Fhir.Core.Features.Operations.Import.ImportResourceParser` (existing Ignixa-only class, implements `IImportResourceParser`, ctor `(IIgnixaJsonSerializer serializer, IResourceWrapperFactory resourceFactory, IIgnixaSchemaContext schemaContext)` — unchanged by this task).
- Produces: `Microsoft.Health.Fhir.Core.Features.Operations.Import.FirelyImportResourceParser` (new class, implements `IImportResourceParser`, ctor `(FhirJsonParser parser, IResourceWrapperFactory resourceFactory)`) — no other task in this plan consumes it.

This task is self-contained (does not touch `FhirModule.cs`, so it has no dependency on Tasks 2/3 and could run in any order — it's listed first because it's the most independent).

- [ ] **Step 1: Create the restored Firely parser class**

The pre-Ignixa-rewrite implementation is recoverable verbatim from git history (`git show 5b0a390df^:src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/ImportResourceParser.cs` — commit `5b0a390df` is the "Ignixa parser" rewrite; its parent `5b0a390df^` has the last pure-Firely version). Its namespace (`Microsoft.Health.Fhir.Core.Features.Operations.Import`) and every type it references (`Resource`, `FhirJsonParser`, `IResourceWrapperFactory`, `ImportMode`, `ImportResource`, `BadRequestException`, `KnownFhirPaths.AzureSoftDeletedExtensionUrl`, `Clock.UtcNow`, `TruncateToMillisecond()`, `.IsSoftDeleted()`, `.ToResourceElement()`) are unchanged Firely-era APIs not touched by any Ignixa migration work, so the restored code should compile as-is under the new class name.

Create `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/FirelyImportResourceParser.cs`:

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EnsureThat;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Health.Core.Extensions;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Operations.Import
{
    /// <summary>
    /// Firely-based <see cref="IImportResourceParser"/> selected by <see cref="Microsoft.Health.Fhir.Core.Configs.FhirSdkMode.Firely"/>.
    /// Restored from the pre-Ignixa implementation (git commit 5b0a390df^) so force-Firely mode has a
    /// working $import path; the Ignixa-based <see cref="ImportResourceParser"/> remains the
    /// implementation used in Hybrid/Ignixa mode.
    /// </summary>
    public class FirelyImportResourceParser : IImportResourceParser
    {
        private static readonly Regex ResourceIdValidationRegex = new Regex(
            "^[A-Za-z0-9\\-\\.]{1,64}$",
            RegexOptions.Compiled);

        private FhirJsonParser _parser;
        private IResourceWrapperFactory _resourceFactory;

        public FirelyImportResourceParser(FhirJsonParser parser, IResourceWrapperFactory resourceFactory)
        {
            _parser = EnsureArg.IsNotNull(parser, nameof(parser));
            _resourceFactory = EnsureArg.IsNotNull(resourceFactory, nameof(resourceFactory));
        }

        public ImportResource Parse(long index, long offset, int length, string rawResource, ImportMode importMode)
        {
            var resource = _parser.Parse<Resource>(rawResource);
            ValidateResourceId(resource?.Id);
            CheckConditionalReferenceInResource(resource, importMode);

            if (resource.Meta == null)
            {
                resource.Meta = new Meta();
            }

            var lastUpdatedIsNull = importMode == ImportMode.InitialLoad || resource.Meta.LastUpdated == null;
            var lastUpdated = lastUpdatedIsNull ? Clock.UtcNow : resource.Meta.LastUpdated.Value;
            resource.Meta.LastUpdated = new DateTimeOffset(lastUpdated.DateTime.TruncateToMillisecond(), lastUpdated.Offset);
            if (!lastUpdatedIsNull && resource.Meta.LastUpdated.Value > Clock.UtcNow.AddSeconds(10)) // 5 sec is the max for the computers in the domain
            {
                throw new NotSupportedException("LastUpdated in the resource cannot be in the future.");
            }

            var keepVersion = true;
            if (lastUpdatedIsNull || string.IsNullOrEmpty(resource.Meta.VersionId) || !int.TryParse(resource.Meta.VersionId, out var _))
            {
                resource.Meta.VersionId = "1";
                keepVersion = false;
            }

            var resourceElement = resource.ToResourceElement();

            var isDeleted = resourceElement.IsSoftDeleted();

            if (isDeleted)
            {
                resource.Meta.RemoveExtension(KnownFhirPaths.AzureSoftDeletedExtensionUrl);
            }

            var resourceWapper = _resourceFactory.Create(resourceElement, isDeleted, true, keepVersion);

            return new ImportResource(index, offset, length, !lastUpdatedIsNull, keepVersion, isDeleted, resourceWapper);
        }

        private static void CheckConditionalReferenceInResource(Resource resource, ImportMode importMode)
        {
            if (importMode == ImportMode.IncrementalLoad)
            {
                return;
            }

            IEnumerable<ResourceReference> references = resource.GetAllChildren<ResourceReference>();
            foreach (ResourceReference reference in references)
            {
                if (string.IsNullOrWhiteSpace(reference.Reference))
                {
                    continue;
                }

                if (reference.Reference.Contains('?', StringComparison.Ordinal))
                {
                    throw new NotSupportedException($"Conditional reference is not supported for $import in {ImportMode.InitialLoad}.");
                }
            }
        }

        private static void ValidateResourceId(string resourceId)
        {
            if (string.IsNullOrWhiteSpace(resourceId) || !ResourceIdValidationRegex.IsMatch(resourceId))
            {
                throw new BadRequestException($"Invalid resource id: '{resourceId ?? "null or empty"}'. " + Core.Resources.IdRequirements);
            }
        }
    }
}
```

If the build (Step 6) reports a compile error in this file (e.g. a type moved namespace since the restored commit), fix only the `using`/qualification needed to resolve it — do not change the restored logic itself.

- [ ] **Step 2: Register the new file in the shared projitems**

Open `src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems` and find the existing entry for the Ignixa parser (search for `Features\Operations\Import\ImportResourceParser.cs`). Add a new line immediately after it, alphabetically adjacent:

```xml
    <Compile Include="$(MSBuildThisFileDirectory)Features\Operations\Import\FirelyImportResourceParser.cs" />
    <Compile Include="$(MSBuildThisFileDirectory)Features\Operations\Import\ImportResourceParser.cs" />
```

(`"FirelyImportResourceParser"` sorts before `"ImportResourceParser"` alphabetically — `F` < `I` — so the new line goes *before* the existing one; read the actual current line to confirm exact surrounding context before inserting, other `Features\Operations\Import\*.cs` entries may already exist near it.)

- [ ] **Step 3: Add a `FhirServerConfiguration` constructor to `OperationsModule` and mode-select the parser**

Read `src/Microsoft.Health.Fhir.Shared.Api/Modules/OperationsModule.cs` first — it currently has no constructor (`public class OperationsModule : IStartupModule` goes straight to `public void Load(IServiceCollection services)`). Add a constructor matching the `SdkModeFeatureModule`/`SecurityModule` pattern, and change the parser registration (currently `services.Add<ImportResourceParser>().Transient().AsSelf().AsImplementedInterfaces();`) to select by mode:

```csharp
        private readonly FhirSdkMode _sdkMode;

        public OperationsModule(FhirServerConfiguration fhirServerConfiguration)
        {
            EnsureArg.IsNotNull(fhirServerConfiguration, nameof(fhirServerConfiguration));
            _sdkMode = fhirServerConfiguration.CoreFeatures.SdkMode;
        }
```

Place this immediately after the class declaration line, before `Load`. Then replace the existing registration:

```csharp
            services.Add<ImportResourceParser>()
                .Transient()
                .AsSelf()
                .AsImplementedInterfaces();
```

with a mode-gated version:

```csharp
            if (_sdkMode == FhirSdkMode.Firely)
            {
                services.Add<FirelyImportResourceParser>()
                    .Transient()
                    .AsSelf()
                    .AsService<IImportResourceParser>();
            }
            else
            {
                services.Add<ImportResourceParser>()
                    .Transient()
                    .AsSelf()
                    .AsService<IImportResourceParser>();
            }
```

(`.AsService<IImportResourceParser>()` replaces `.AsImplementedInterfaces()` here because `IImportResourceParser` is the only interface either class implements — verify this is true for `ImportResourceParser` by checking its class declaration; if it implements additional interfaces consumed elsewhere, use `.AsImplementedInterfaces()` on both branches instead to preserve current behavior exactly.)

Add the two new `using` lines this requires, in alphabetical order among the file's existing usings: `using Microsoft.Health.Fhir.Api.Configs;` (for `FhirServerConfiguration`) and `using Microsoft.Health.Fhir.Core.Configs;` (for `FhirSdkMode`) — read the file's current using block first to find the exact correct alphabetical insertion points; do not guess positions.

- [ ] **Step 4: Write `OperationsModuleTests.cs`**

Follow `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/FeatureFlags/SdkMode/SdkModeFeatureModuleTests.cs` exactly as the harness pattern (read it first): construct `new FhirServerConfiguration()`, set `.CoreFeatures.SdkMode = mode`, instantiate the module directly (not via `RegisterAssemblyModules`), call `Load(services)` on a fresh `ServiceCollection` pre-populated with the constructor dependencies the registered class needs (`FhirJsonParser`, `IIgnixaJsonSerializer`, `IResourceWrapperFactory`, `IIgnixaSchemaContext` — stub/fake as needed, following how `SdkModeFeatureModuleTests` stubs formatter dependencies), build the provider, resolve `IImportResourceParser`, and assert its concrete type.

Create `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/OperationsModuleTests.cs`, and add its `<Compile Include>` entry to `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems` (this repo's `.projitems` files use explicit per-file `<Compile Include>` entries, not wildcard globs — a new file that isn't listed there will not compile into the test assembly, and `dotnet test` in Step 5 would silently run zero of the new tests rather than fail). Read the projitems file first: `SdkModeFeatureModuleTests.cs` (added by earlier work on this branch) is already listed under a `Modules\...` entry — insert `Modules\OperationsModuleTests.cs` alphabetically relative to it and any other `Modules\...` entries already present, e.g.:

```xml
    <Compile Include="$(MSBuildThisFileDirectory)Modules\OperationsModuleTests.cs" />
```

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Api.Modules;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Operations.Import;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Resources;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Modules
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public class OperationsModuleTests
    {
        [Fact]
        public void GivenFirelyMode_WhenLoaded_ThenFirelyImportResourceParserIsRegistered()
        {
            var provider = BuildProvider(FhirSdkMode.Firely);

            var parser = provider.GetRequiredService<IImportResourceParser>();

            Assert.IsType<FirelyImportResourceParser>(parser);
        }

        [Theory]
        [InlineData(FhirSdkMode.Hybrid)]
        [InlineData(FhirSdkMode.Ignixa)]
        public void GivenHybridOrIgnixaMode_WhenLoaded_ThenIgnixaImportResourceParserIsRegistered(FhirSdkMode mode)
        {
            var provider = BuildProvider(mode);

            var parser = provider.GetRequiredService<IImportResourceParser>();

            Assert.IsType<ImportResourceParser>(parser);
        }

        [Fact]
        public void GivenNullConfiguration_WhenConstructed_ThenThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new OperationsModule(null));
        }

        private static IServiceProvider BuildProvider(FhirSdkMode mode)
        {
            var fhirServerConfiguration = new FhirServerConfiguration();
            fhirServerConfiguration.CoreFeatures.SdkMode = mode;

            var services = new ServiceCollection();
            services.AddSingleton(Substitute.For<Hl7.Fhir.Serialization.FhirJsonParser>());
            services.AddSingleton(Substitute.For<IIgnixaJsonSerializer>());
            services.AddSingleton(Substitute.For<IResourceWrapperFactory>());
            services.AddSingleton(Substitute.For<IIgnixaSchemaContext>());

            new OperationsModule(fhirServerConfiguration).Load(services);

            return services.BuildServiceProvider();
        }
    }
}
```

`Hl7.Fhir.Serialization.FhirJsonParser` is a concrete (non-virtual-by-default) class — if `Substitute.For<FhirJsonParser>()` fails at runtime (NSubstitute requires a parameterless constructor and virtual members to proxy a concrete class), replace that one line with `services.AddSingleton(new Hl7.Fhir.Serialization.FhirJsonParser());` instead (a real instance is fine here since the test never calls `Parse`). Only `OperationsModule.Load` needs to run to completion and let DI resolve `IImportResourceParser` — the module registers the class as `Transient`, so its constructor dependencies must be resolvable, but none of them need to be functional for this test.

- [ ] **Step 5: Run the new tests**

```
dotnet build src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

Expected: build succeeds, all tests pass including the four new `OperationsModuleTests` (note: this test runner ignores `--filter`, per prior experience in this repo — it runs the whole assembly; check the emitted TRX or console summary for the new test names rather than relying on a filtered subset).

- [ ] **Step 6: Full solution build**

```
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
```

Expected: `0 Warning(s)` beyond pre-existing, and the only errors (if any) are the four pre-existing E2E SDK-version environment failures described in Global Constraints.

- [ ] **Step 7: Commit**

```bash
git add src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/FirelyImportResourceParser.cs \
        src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems \
        src/Microsoft.Health.Fhir.Shared.Api/Modules/OperationsModule.cs \
        src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/OperationsModuleTests.cs \
        src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems
git commit -m "US-6: Restore Firely \$import parser behind SdkMode.Firely"
```

---

### Task 2: US-7 — Flag-gate DB-read JSON deserialization

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs`
- Test: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/FhirModuleTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems`

**Interfaces:**
- Consumes: `Microsoft.Health.Fhir.Core.Configs.FhirSdkMode`, `Microsoft.Health.Fhir.Api.Configs.FhirServerConfiguration`.
- Produces: `FhirModule` gains a constructor `FhirModule(FhirServerConfiguration fhirServerConfiguration)` and a private `_sdkMode` field of type `FhirSdkMode` — **Task 3 depends on this constructor and field already existing** (Task 3 extends the same constructor rather than adding a second one). If Task 3 is dispatched after this task's commit lands, its implementer will see the constructor already present in the current file — do not add a second constructor or a second `_sdkMode` field in Task 3.

- [ ] **Step 1: Add the `FhirServerConfiguration` constructor to `FhirModule`**

Read `src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs` first. It currently has no constructor — `public class FhirModule : IStartupModule` (around line 47) goes straight to `public void Load(IServiceCollection services)`. Add:

```csharp
        private readonly FhirSdkMode _sdkMode;

        public FhirModule(FhirServerConfiguration fhirServerConfiguration)
        {
            EnsureArg.IsNotNull(fhirServerConfiguration, nameof(fhirServerConfiguration));
            _sdkMode = fhirServerConfiguration.CoreFeatures.SdkMode;
        }
```

immediately after the class declaration, before `Load`. Add the two new usings this requires: `using Microsoft.Health.Fhir.Api.Configs;` (insert alphabetically — it sorts immediately before `using Microsoft.Health.Fhir.Api.Features.Context;`, which is already present) and `using Microsoft.Health.Fhir.Core.Configs;` (insert alphabetically — it sorts immediately before `using Microsoft.Health.Fhir.Core.Extensions;`, which is already present). Read the file's actual current using block to confirm these exact neighbor lines before inserting — do not guess.

- [ ] **Step 2: Flag-gate the JSON branch of the DB-read deserializer dictionary**

Find the `services.AddSingleton<IReadOnlyDictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>>(provider => { ... })` registration (currently ~lines 108-139; search for `FhirResourceFormat.Json,` to locate it precisely, since line numbers have shifted). It builds a `Dictionary` with two entries, `FhirResourceFormat.Json` (currently always Ignixa-based) and `FhirResourceFormat.Xml` (already Firely-based, using the file's existing `xmlParser` closure variable and local `SetMetadata` function). Change the `Json` entry to branch on `_sdkMode`, using the file's existing `jsonParser` closure variable (already captured earlier in `Load`, e.g. `var jsonParser = new FhirJsonParser(...)`) and the existing `SetMetadata` local function — do not add a new parser instance or a new metadata-setting helper, reuse what's already in scope exactly as the `Xml` branch does:

```csharp
                return new Dictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>
                {
                    {
                        FhirResourceFormat.Json, (str, version, lastModified) =>
                        {
                            if (_sdkMode == FhirSdkMode.Firely)
                            {
                                var resource = jsonParser.Parse<Resource>(str);
                                return SetMetadata(resource, version, lastModified);
                            }

                            // Use Ignixa for JSON deserialization
                            var resourceNode = ignixaSerializer.Parse(str);
                            var ignixaElement = new IgnixaResourceElement(resourceNode, schemaContext.Schema);

                            // Set metadata
                            ignixaElement.SetVersionId(version);
                            ignixaElement.SetLastUpdated(lastModified);

                            // Convert to ResourceElement for backward compatibility
                            return new ResourceElement(ignixaElement.ToTypedElement());
                        }
                    },
                    {
                        FhirResourceFormat.Xml, (str, version, lastModified) =>
                        {
                            var resource = xmlParser.Parse<Resource>(str);

                            return SetMetadata(resource, version, lastModified);
                        }
                    },
                };
```

Only the body of the `FhirResourceFormat.Json` case changes (adding the `if (_sdkMode == FhirSdkMode.Firely) { ... }` branch at the top); the `Xml` case and everything else in the surrounding factory lambda (the `ignixaSerializer`/`schemaContext` resolution lines above this `return`) stays exactly as it is today — read the current file to confirm you are preserving those lines unchanged, this snippet only shows the returned `Dictionary` literal for clarity.

- [ ] **Step 3: Write `FhirModuleTests.cs`**

Follow the same harness pattern as `SdkModeFeatureModuleTests.cs`: build a `ServiceCollection`, register whatever `FhirModule.Load` needs to complete without throwing (this is a large `Load` method with many registrations unrelated to this task — the test only needs to resolve `IReadOnlyDictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>` afterward, so register real or stub instances for every service `Load` unconditionally constructs or resolves eagerly; anything resolved lazily inside a factory delegate — like `IIgnixaJsonSerializer`/`IIgnixaSchemaContext` inside the dictionary's own factory — only needs to be registered, not functional, since the test won't invoke the `Ignixa` branch of the function it resolves).

Create `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/FhirModuleTests.cs`, and add its `<Compile Include>` entry to `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems`, alphabetically among the `Modules\...` entries (same rationale and location pattern as Task 1 Step 4 — read the file's current state first, since Task 1's `Modules\OperationsModuleTests.cs` entry should already be present if Task 1 ran before this task):

```xml
    <Compile Include="$(MSBuildThisFileDirectory)Modules\FhirModuleTests.cs" />
```

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Api.Modules;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Ignixa;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Modules
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public class FhirModuleTests
    {
        [Fact]
        public void GivenFirelyMode_WhenDeserializingJson_ThenFirelyParserIsUsed()
        {
            var provider = BuildProvider(FhirSdkMode.Firely);
            var deserializers = provider.GetRequiredService<IReadOnlyDictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>>();

            var patientJson = "{\"resourceType\":\"Patient\",\"id\":\"test\"}";
            var element = deserializers[FhirResourceFormat.Json](patientJson, "1", DateTimeOffset.UtcNow);

            Assert.Equal("Patient", element.InstanceType);
            Assert.Null(GetIgnixaNode(element));
        }

        [Theory]
        [InlineData(FhirSdkMode.Hybrid)]
        [InlineData(FhirSdkMode.Ignixa)]
        public void GivenHybridOrIgnixaMode_WhenDeserializingJson_ThenIgnixaParserIsUsed(FhirSdkMode mode)
        {
            var provider = BuildProvider(mode);
            var deserializers = provider.GetRequiredService<IReadOnlyDictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>>();

            var patientJson = "{\"resourceType\":\"Patient\",\"id\":\"test\"}";
            var element = deserializers[FhirResourceFormat.Json](patientJson, "1", DateTimeOffset.UtcNow);

            Assert.Equal("Patient", element.InstanceType);
            Assert.NotNull(GetIgnixaNode(element));
        }

        private static object GetIgnixaNode(ResourceElement element)
        {
            return element.GetIgnixaNode();
        }

        private static IServiceProvider BuildProvider(FhirSdkMode mode)
        {
            var fhirServerConfiguration = new FhirServerConfiguration();
            fhirServerConfiguration.CoreFeatures.SdkMode = mode;

            var services = new ServiceCollection();

            new FhirModule(fhirServerConfiguration).Load(services);

            return services.BuildServiceProvider();
        }
    }
}
```

`element.GetIgnixaNode()` refers to the extension method in `Microsoft.Health.Fhir.Shared.Core/Extensions/ResourceElementIgnixaExtensions.cs` (already used elsewhere in this codebase, e.g. `RawResourceFactory`) — it returns the `ResourceJsonNode` if the element carries one, or `null` for a Firely-only-backed `ResourceElement`; add `using Microsoft.Health.Fhir.Core.Extensions;` if that namespace isn't already covered by one of the usings above (check whether `ResourceElementIgnixaExtensions` is in `Microsoft.Health.Fhir.Core.Extensions` or a different namespace by reading that file first — do not assume).

**This test will very likely fail to compile or fail at runtime on the first attempt** because `FhirModule.Load` registers dozens of services unrelated to this task, some of which may themselves require dependencies not stubbed above (the method spans conformance providers, filters, formatters, etc.). If `services.BuildServiceProvider()` or resolving the dictionary throws `InvalidOperationException` for an unresolvable service, this most likely means `Load` eagerly resolves something during registration (rare, but check for any `services.AddSingleton<T>(implementationInstance)` pattern that itself calls a constructor at registration time) — if so, add the minimal stub/registration needed and continue; do not weaken the assertions to work around a genuine wiring problem.

- [ ] **Step 4: Run the new tests**

```
dotnet build src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

Expected: build succeeds, all tests pass including the three new `FhirModuleTests`.

- [ ] **Step 5: Full solution build**

```
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
```

Expected: same as Task 1 Step 6.

- [ ] **Step 6: Commit**

```bash
git add src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs \
        src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/FhirModuleTests.cs \
        src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems
git commit -m "US-7: Flag-gate DB-read JSON deserialization behind SdkMode.Firely"
```

---

### Task 3: US-8 — Flag-gate FHIRPath provider and model validator selection

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs` (reuses the constructor/`_sdkMode` field added in Task 2 — **do not add a second constructor**; read the current file first to confirm Task 2 already landed)
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/ValidationModule.cs`
- Test: extend `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/FhirModuleTests.cs` (from Task 2, no new projitems entry needed), create `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/ValidationModuleTests.cs` (new file, needs a projitems entry)
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems`

**Interfaces:**
- Consumes: `FhirModule._sdkMode` (already present from Task 2 — verify by reading the current file before editing), `Microsoft.Health.Fhir.Core.Configs.FhirSdkMode`, `Microsoft.Health.Fhir.Api.Configs.FhirServerConfiguration`.
- Produces: `ValidationModule` gains a constructor `ValidationModule(FhirServerConfiguration fhirServerConfiguration)` and a private `_sdkMode` field — no other task in this plan consumes it.

Do not modify `SearchModule.cs` in this task. Research confirmed `SearchModule`'s `TryAddSingleton<IFhirPathProvider, FirelyFhirPathProvider>()` (line ~147) already exists and is a correct fallback — the reason it's currently inert is that `FhirModule`'s unconditional `AddIgnixaFhirPath(...)` call always wins the registration race (via `RemoveAll<IFhirPathProvider>()` followed by its own `AddSingleton`, regardless of module load order). Skipping that call in Firely mode is the only change needed to make `SearchModule`'s existing fallback take effect — do not add new logic to `SearchModule`.

- [ ] **Step 1: Skip `AddIgnixaFhirPath` in Firely mode**

Read `src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs` first and confirm the constructor/`_sdkMode` field from Task 2 is present (`git log --oneline -- src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs` should show the Task 2 commit). Find:

```csharp
            // Register Ignixa FHIRPath provider for high-performance search indexing
            // Uses delegate compilation for ~80% of common patterns, ~10x faster than Firely
            services.AddIgnixaFhirPath(provider => provider.GetRequiredService<IIgnixaSchemaContext>().Schema);
```

(currently ~line 106, search for `AddIgnixaFhirPath` to locate it precisely — line numbers shifted after Task 2). Wrap the call:

```csharp
            // Register Ignixa FHIRPath provider for high-performance search indexing
            // Uses delegate compilation for ~80% of common patterns, ~10x faster than Firely.
            // Skipped in Firely mode so SearchModule's FirelyFhirPathProvider fallback registration
            // (TryAddSingleton, which no-ops if a provider is already registered) takes effect instead.
            if (_sdkMode != FhirSdkMode.Firely)
            {
                services.AddIgnixaFhirPath(provider => provider.GetRequiredService<IIgnixaSchemaContext>().Schema);
            }
```

Do not touch the `IIgnixaSchemaContext` registration two lines above this (`services.AddSingleton<IIgnixaSchemaContext, IgnixaSchemaContext>();`) — per Global Constraints, it must stay unconditional in every mode.

- [ ] **Step 2: Extend `FhirModuleTests.cs` with FHIRPath provider assertions**

This test needs `SearchModule.Load` to also run (since the Firely fallback lives there, not in `FhirModule`), so it differs from the existing tests in that file — it composes both modules against one `ServiceCollection`, the same way the real application does via `RegisterAssemblyModules`. Add to `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/FhirModuleTests.cs` (read the file's current top-of-class contents first, insert these as additional `[Fact]`/`[Theory]` methods alongside the existing ones from Task 2 — do not duplicate the `BuildProvider` helper, add a second helper method instead since this scenario needs `SearchModule` too):

```csharp
        [Fact]
        public void GivenFirelyMode_WhenSearchModuleAlsoLoaded_ThenFirelyFhirPathProviderIsRegistered()
        {
            var provider = BuildProviderWithSearchModule(FhirSdkMode.Firely);

            var fhirPathProvider = provider.GetRequiredService<Microsoft.Health.Fhir.Core.Features.Search.FhirPath.IFhirPathProvider>();

            Assert.IsType<Microsoft.Health.Fhir.Core.Features.Search.FhirPath.FirelyFhirPathProvider>(fhirPathProvider);
        }

        [Theory]
        [InlineData(FhirSdkMode.Hybrid)]
        [InlineData(FhirSdkMode.Ignixa)]
        public void GivenHybridOrIgnixaMode_WhenSearchModuleAlsoLoaded_ThenIgnixaFhirPathProviderIsRegistered(FhirSdkMode mode)
        {
            var provider = BuildProviderWithSearchModule(mode);

            var fhirPathProvider = provider.GetRequiredService<Microsoft.Health.Fhir.Core.Features.Search.FhirPath.IFhirPathProvider>();

            Assert.IsType<IgnixaFhirPathProvider>(fhirPathProvider);
        }

        private static IServiceProvider BuildProviderWithSearchModule(FhirSdkMode mode)
        {
            var fhirServerConfiguration = new FhirServerConfiguration();
            fhirServerConfiguration.CoreFeatures.SdkMode = mode;

            var services = new ServiceCollection();

            new FhirModule(fhirServerConfiguration).Load(services);
            new Microsoft.Health.Fhir.Api.Modules.SearchModule(fhirServerConfiguration).Load(services);

            return services.BuildServiceProvider();
        }
```

`IgnixaFhirPathProvider` is `internal` to the `Microsoft.Health.Fhir.Ignixa` namespace/assembly-family (Core layer) — confirm via `Assert.IsType<IgnixaFhirPathProvider>(...)` whether this test project can name it directly (check `src/Microsoft.Health.Fhir.Core/Properties/AssemblyInfo.cs`'s `InternalsVisibleTo` list, and any Ignixa-specific `AssemblyInfo.cs`, for whether `Microsoft.Health.Fhir.Api.UnitTests`/`Microsoft.Health.Fhir.R4.Api.UnitTests` is covered — this is the exact same visibility question already resolved for `SdkModeFeatureModuleTests.cs`, which had to avoid naming `IgnixaFhirJsonInputFormatter`/`IgnixaFhirJsonOutputFormatter` directly for this reason). If it's not visible, replace `Assert.IsType<IgnixaFhirPathProvider>(fhirPathProvider)` with `Assert.NotEqual(typeof(Microsoft.Health.Fhir.Core.Features.Search.FhirPath.FirelyFhirPathProvider), fhirPathProvider.GetType())` plus `Assert.Equal("IgnixaFhirPathProvider", fhirPathProvider.GetType().Name)` (asserting by type name string, the same workaround `SdkModeFeatureModuleTests.cs` uses), and remove the unneeded direct reference/using.

`SearchModule.Load` may itself require additional stub registrations beyond what `FhirModule.Load` already provides in this test — if `BuildServiceProvider()`/resolution throws, read `SearchModule.cs` to find what's missing and add the minimal stub, following the same judgment call as Task 2 Step 3's last paragraph.

- [ ] **Step 3: Add the `FhirServerConfiguration` constructor to `ValidationModule` and mode-select the validator**

Read `src/Microsoft.Health.Fhir.Shared.Api/Modules/ValidationModule.cs` first — it currently has no constructor. Add:

```csharp
        private readonly FhirSdkMode _sdkMode;

        public ValidationModule(FhirServerConfiguration fhirServerConfiguration)
        {
            EnsureArg.IsNotNull(fhirServerConfiguration, nameof(fhirServerConfiguration));
            _sdkMode = fhirServerConfiguration.CoreFeatures.SdkMode;
        }
```

immediately after the class declaration, before `Load`. Add `using Microsoft.Health.Fhir.Api.Configs;` and `using Microsoft.Health.Fhir.Core.Configs;` in alphabetical order among the file's existing usings (read the current using block first).

Find the existing validator registration:

```csharp
        // Register the Firely-based validator as a fallback for non-Ignixa resources
        services.AddSingleton<ModelAttributeValidator>();

        // Register the Ignixa-based validator as the primary IModelAttributeValidator
        // Uses fast-path validation (Tier 1-2) for Ignixa resources (~1-5ms)
        // Falls back to Firely DotNetAttributeValidation for non-Ignixa resources
        services.AddSingleton<IModelAttributeValidator>(sp =>
        {
            var schemaContext = sp.GetRequiredService<IIgnixaSchemaContext>();
            var fallbackValidator = sp.GetRequiredService<ModelAttributeValidator>();
            return new IgnixaResourceValidator(schemaContext, fallbackValidator);
        });
```

Replace the second block (leave the plain `services.AddSingleton<ModelAttributeValidator>();` registration exactly as-is — the Firely mode branch below still needs to resolve it) with a mode-gated version:

```csharp
        // Register the Ignixa-based validator as the primary IModelAttributeValidator
        // Uses fast-path validation (Tier 1-2) for Ignixa resources (~1-5ms)
        // Falls back to Firely DotNetAttributeValidation for non-Ignixa resources.
        // In Firely mode, ModelAttributeValidator itself is registered directly instead, so no
        // resource ever takes the Ignixa fast path.
        if (_sdkMode == FhirSdkMode.Firely)
        {
            services.AddSingleton<IModelAttributeValidator>(sp => sp.GetRequiredService<ModelAttributeValidator>());
        }
        else
        {
            services.AddSingleton<IModelAttributeValidator>(sp =>
            {
                var schemaContext = sp.GetRequiredService<IIgnixaSchemaContext>();
                var fallbackValidator = sp.GetRequiredService<ModelAttributeValidator>();
                return new IgnixaResourceValidator(schemaContext, fallbackValidator);
            });
        }
```

- [ ] **Step 4: Write `ValidationModuleTests.cs`**

Create `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/ValidationModuleTests.cs`, and add its `<Compile Include>` entry to `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems`, alphabetically among the `Modules\...` entries (same pattern as Tasks 1-2; by this point `Modules\FhirModuleTests.cs` and `Modules\OperationsModuleTests.cs` should already be listed — `ValidationModuleTests.cs` sorts after both):

```xml
    <Compile Include="$(MSBuildThisFileDirectory)Modules\ValidationModuleTests.cs" />
```

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Api.Modules;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Validation;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Modules
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Web)]
    public class ValidationModuleTests
    {
        [Fact]
        public void GivenFirelyMode_WhenLoaded_ThenModelAttributeValidatorIsUsedDirectly()
        {
            var provider = BuildProvider(FhirSdkMode.Firely);

            var validator = provider.GetRequiredService<IModelAttributeValidator>();

            Assert.IsType<ModelAttributeValidator>(validator);
        }

        [Theory]
        [InlineData(FhirSdkMode.Hybrid)]
        [InlineData(FhirSdkMode.Ignixa)]
        public void GivenHybridOrIgnixaMode_WhenLoaded_ThenIgnixaResourceValidatorWrapsIt(FhirSdkMode mode)
        {
            var provider = BuildProvider(mode);

            var validator = provider.GetRequiredService<IModelAttributeValidator>();

            Assert.IsType<IgnixaResourceValidator>(validator);
        }

        [Fact]
        public void GivenNullConfiguration_WhenConstructed_ThenThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new ValidationModule(null));
        }

        private static IServiceProvider BuildProvider(FhirSdkMode mode)
        {
            var fhirServerConfiguration = new FhirServerConfiguration();
            fhirServerConfiguration.CoreFeatures.SdkMode = mode;

            var services = new ServiceCollection();

            new ValidationModule(fhirServerConfiguration).Load(services);

            return services.BuildServiceProvider();
        }
    }
}
```

`ValidationModule.Load` also registers `IProfileValidator`, `ServerProvideProfileValidation`, and every `IValidator` in the assembly via `services.TypesInSameAssembly(...)` — if any of these fail to construct during `BuildServiceProvider()`/resolution (unlikely, since `AddSingleton` factories are lazy and this test never resolves them), read the file to see what's needed and stub minimally, same judgment call as prior tasks.

- [ ] **Step 5: Run the new/extended tests**

```
dotnet build src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

Expected: build succeeds, all tests pass, including the extended `FhirModuleTests` and new `ValidationModuleTests`.

- [ ] **Step 6: Full solution build**

```
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
```

Expected: same as prior tasks.

- [ ] **Step 7: Commit**

```bash
git add src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs \
        src/Microsoft.Health.Fhir.Shared.Api/Modules/ValidationModule.cs \
        src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/FhirModuleTests.cs \
        src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/ValidationModuleTests.cs \
        src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Microsoft.Health.Fhir.Shared.Api.UnitTests.projitems
git commit -m "US-8: Flag-gate FHIRPath provider and model validator selection behind SdkMode"
```

---

## After All Tasks

Once Tasks 1-3 are complete and reviewed, `SdkMode.Firely` produces a genuinely Firely-only runtime across every seam this plan covers (import parsing, DB-read deserialization, FHIRPath evaluation, model validation) while `Hybrid`/`Ignixa` remain unchanged. This completes user-stories.md's Phase 2 ("Force-Firely to green"). Remaining Phase 2 work not in this plan: **US-9** (parse-strictness parity corpus across all three modes) — out of scope here since it's a cross-cutting test-corpus story that depends on all of US-6/7/8 being done first, not a single module's registration change; write it as a separate plan once this one is merged.
