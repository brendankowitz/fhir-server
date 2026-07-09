# ADR-2607 Adversarial Review Signoff

**Reviewer**: Independent second-opinion review (no involvement in producing the investigations, ADR, or backlog)
**Date**: 2026-07-08
**Scope reviewed**: `adr-2607-ignixa-merge-readiness.md`, `user-stories.md`, and the five 2026-07-08 investigations under `investigations/`
**Method**: Every reviewed claim was re-verified against the working tree at branch `personal/bkowitz/ignixa-sdk-next-steps-fable`, the `E:\data\src\ignixa-fhir` checkout (via git tags, not the working tree), nuget.org, and an empirical ASP.NET Core repro — not against the documents' own prose.

## Verdict: **Signed off with corrections**

The decision, the G1-G28 gap register, and the 30-story backlog are sound and safe to start executing. Every correction found was in the *investigations*, not in the ADR or backlog — the synthesis resolved the investigations' internal contradictions in the right direction in each case. Corrections and residual notes are listed at the end.

---

## 1. The headline claim (G1) — independently re-verified, TRUE

**Claim**: the Ignixa MVC JSON formatters are registered but never selected at runtime; the HTTP boundary is 100% Firely; the test-readiness report's "E2E validated" claim is false.

**What I verified, end-to-end:**

1. **Registration types.** `IgnixaFormatterConfiguration` is `IConfigureOptions<MvcOptions>` (`src/Microsoft.Health.Fhir.Shared.Core/Ignixa/ServiceCollectionExtensions.cs:103,148`), inserting both Ignixa formatters at index 0 (`:164-165`). `FormatterConfiguration` is `IPostConfigureOptions<MvcOptions>` (`FormatterConfiguration.cs:17`, registered `FhirModule.cs:146-149`), inserting every DI-registered `TextInputFormatter`/`TextOutputFormatter` at indices 0..n-1 (`FormatterConfiguration.cs:38-46`).
2. **The Ignixa formatters are not in the PostConfigure arrays.** They are registered as concrete singletons only (`ServiceCollectionExtensions.cs:60-69`), never `AsService<TextInputFormatter>`, so `FormatterConfiguration` cannot re-insert them; only the legacy Firely JSON formatters (`FhirModule.cs:166-174`), the XML formatters (`XmlFormatterFeatureModule.cs:37-47`), and the HTML formatter flow through it.
3. **No other configurator interferes.** A solution-wide sweep found exactly one `IConfigureOptions<MvcOptions>` (Ignixa's) and three other `MvcOptions` post-configurations (`MvcModule.cs:36`, `ValidationModule.cs:28`, `ValidatePostConfigureOptions`) — none touches the formatter lists.
4. **Framework ordering, empirically.** I built and ran a minimal net9.0 repro replicating the exact registration pattern (concrete formatters + `IPostConfigureOptions` inserting at 0..n-1 + `IConfigureOptions` inserting at 0). Result: `[0] Legacy, [1] Ignixa` for both input and output lists. The options pipeline runs all `IConfigureOptions` before any `IPostConfigureOptions`, regardless of DI registration order.
5. **The front formatter claims everything controllers use.** Legacy input claims `Resource` + `ResourceElement` (`FhirJsonInputFormatter.cs:43-49`); legacy output claims `Resource` + `RawResourceElement` (`FhirJsonOutputFormatter.cs:63-68`). Controllers bind `ResourceElement` (CRUD, `FhirController.cs:169/193/246/266`) or `Resource`/`Parameters` (bundles at `:725`, operations), and `FhirResult.GetResultToSerialize()` emits only POCOs or `RawResourceElement` (`FhirResult.cs:162-176`). MVC selects the first formatter whose `CanRead`/`CanWrite` passes. The Ignixa formatters *do* also claim `ResourceElement`/`Resource` (`IgnixaFhirJsonInputFormatter.cs:130-133`, `IgnixaFhirJsonOutputFormatter.cs:102-105`) — they lose purely on order, which confirms both that the defect is real and that fixing the order genuinely activates them (as G1/G3 assume).
6. **The falsified prior claim exists as described.** `reports/ignixa-test-readiness-report.md:156,239` states BulkUpdate E2E validated "the full HTTP pipeline with Ignixa formatters active." Given 1-5, those runs exercised the Firely formatters. The ADR's "supersedes stale claims" section is accurate.

**Conclusion: G1 is correct, correctly severity-ranked, and correctly chosen as the #1 item.** The ADR's fix shape (delete the `IConfigureOptions` path, make `FormatterConfiguration.PostConfigure` the single owner, order-asserting test) attacks the actual mechanism.

## 2. Additional claims re-verified (sample across all five investigations)

| Claim (register ID) | Source investigation | Result |
|---|---|---|
| G5: pin is 0.0.163; validation ships in 0.6.7; `Ignixa.PackageManagement` unreferenced | validation-sdk-dependency | **Confirmed.** `Directory.Packages.props:10` = 0.0.163 (tag dated 2026-02-10); `PackageBackedValidator.cs` absent at tag `release/0.0.163`, present at `release/0.6.7`; PR #310 commit `64195494` is an ancestor of `release/0.6.7`; nuget.org flat-container lists 0.6.7 as latest for `Ignixa.PackageManagement` and `Ignixa.Validation`; no `Ignixa.PackageManagement` entry in `Directory.Packages.props` |
| G9: validator re-runs Firely on success; 14 conformance types bypass Ignixa | dual-provider + validation-sdk | **Confirmed.** `IgnixaResourceValidator.cs:182` returns `_fallbackValidator.TryValidate(...)` after a *successful* Ignixa validation; the `ConformanceResourceTypes` set (`:51-67`) contains exactly 14 types |
| G7: `$import` Firely parser deleted; registrations unconditional; no flag exists | dual-provider + abstraction-audit | **Confirmed.** `ImportResourceParser.cs` is Ignixa-only with no Firely fallback; `ValidationModule.cs:48-53` and `FhirModule.cs:102/106/180` register unconditionally; zero `Ignixa`/`SdkMode` hits in any `appsettings*.json` |
| G3: Ignixa output formatter has no raw-bundle handling | shim-minimization-audit | **Confirmed.** Zero `RawBundleEntryComponent`/`BundleSerializer` references in `IgnixaFhirJsonOutputFormatter.cs`; since it claims `Resource` (incl. `Bundle`), a naive G1 fix would route search bundles to it — G3 as co-requisite is right |
| G8: DB-read node drop | shim audit + dual-provider | **Confirmed.** `FhirModule.cs:127` uses the one-arg `ResourceElement` ctor; `ResourceElement.cs:29-43` shows only the two-arg ctor sets `ResourceInstance`; `GetIgnixaNode()` reads `ResourceInstance` (`ResourceElementIgnixaExtensions.cs:29-39`) |
| G14: ad-hoc FHIRPath bypasses the provider; Ignixa path parses uncached | abstraction-audit | **Confirmed.** `ResourceElement.Scalar/Select/Predicate` (`:90-104`) call Firely `Hl7.FhirPath` extensions directly; `IgnixaResourceElement.Scalar/Select/Predicate` call `FhirPathParser.Parse(fhirPath)` per invocation with no cache |
| G4: bundle pipeline Firely end-to-end | dual-provider + shim audit | **Confirmed (spot).** `FhirController.cs:725` binds Firely `Resource`; `BundleHandler.cs:212` does `ToPoco<Hl7.Fhir.Model.Bundle>()` |
| G26: dead code claims | all three code audits | **Confirmed.** `AddIgnixaPersistence` has zero call sites; `Ignixa.Extensions.FirelySdk6`/`Ignixa.Search` pins have zero csproj consumers; `Shared.Core.projitems:14-16` compiles only 3 of 10 files in `Shared.Core/Ignixa`; hash comparison of the 7 dead copies: 6 identical, `IgnixaCompiledFhirPath.cs` drifted |
| XML: `SupportsXml` default-on; Ignixa has zero XML | xml-pipeline-ignixa-adoption | **Confirmed.** `Shared.Web/appsettings.json:21` ships `true`; zero case-insensitive `xml` hits in `src/Core/Ignixa.Serialization` at tag `release/0.6.7` |
| STU3/R4B schema-provider bug fixed | dual-provider | **Confirmed.** `IgnixaSchemaContext.cs:69-79` maps all four versions to the correct providers |

## 3. External-blocked stories (US-E1..E6) — blockers are real, not stale

Verified against ignixa-fhir tag `release/0.6.7` (`1f0f659d`, 2026-07-08 — the newest published version), so these are current as of today:

- **US-E1** (`_summary`/`_elements`): `ResourceElementsSerializer.cs` exists only under `src/Application/Ignixa.Application/` (app layer, unshipped). Real.
- **US-E2** (patch): `FhirPatchEngine.cs` exists only under `src/Application/Ignixa.Application/Features/Patch/`. Real.
- **US-E3** (XML): zero XML source in `Ignixa.Serialization`. Real.
- **US-E4** (profile-validation default): parity gaps are upstream-documented; the deferral is exit-criterion-gated on the US-20 harness. Reasonable.
- **US-E5** (terminology): `InMemoryTerminologyService.ExpandValueSetAsync` returns `Task.FromResult<ExpandResult?>(null)` and `TranslateCodeAsync` returns "not supported" — the "stubbed" characterization is literal. Real.
- **US-E6** (anonymizer): external Firely-POCO packages; not an ignixa-fhir concern. Real.

One nuance, not an error: `BundleJsonNode.cs` already exists at tag `release/0.0.163`, so **US-15 is not hard-blocked by US-3** — the "blocked by US-3" edge is prudence (don't build bundle assembly against a 5-month-stale API), which is a defensible sequencing choice.

## 4. Sequencing sanity

Spot-checked dependency edges: US-2 co-requisite of US-1 (correct — verified the Ignixa output formatter would claim `Bundle` after the order flip and would emit null entries); US-10 ← US-1/US-2 (correct — neither formatter claims `ResourceElement` for output today, per the code); US-11 ← US-3 (correct — the exclusion-list re-test needs 0.6.7); US-16 ← US-15, US-18/19 ← US-17, US-24 last (all consistent with the layer structure). US-9's "needs all three modes wired" caveat is honest about spanning phases 2-3. No cycles, no inverted edges found.

## 5. Deferral policy (objective 5)

Every deferral in the ADR's carve-out table names a concrete blocking condition and an exit criterion; none reads as rationalization. The two closest calls are defensible: G23 (conformance) argues raw-JSON construction is *worse* than the POCO and names typed builders as the exit; the E2E Firely client is declared permanent with a stated reason (cross-SDK conformance check) and is consistent with the feature readme's scope exclusion.

## 6. Missing-gap review

Areas probed for omissions, given the five objectives:

- **Subscriptions / GraphQL**: confirmed absent from `src/` — correctly excluded.
- **SMART/authorization**: `SmartClinicalScopesMiddleware`/`ScopeRestriction` carry type-only Firely usage (abstraction audit §3.14, Low). Not an explicit G-row; falls under the objective-3 catch-alls (G27/G28). Acceptable, but an implementer of US-24 should sweep it.
- **Version-specific behavior under the flag**: covered (schema-provider mapping verified for all four versions; Firely-mode rule for `IgnixaSchemaContext` eager construction is in US-8).
- **Custom operations**: $convert-data (SDK-independent), $everything/$member-match/$reindex/bulk ops all mapped to G-rows.
- **Not in the register, worth carrying as an implementation note on US-1**: `FormatterConfiguration`'s injected arrays also carry the **XML and HTML** output formatters. The single-owner rewrite must preserve their relative placement in all three modes, and the order-asserting test should cover them, not just the two JSON stacks.

No missing gap rises to register-worthiness; the G1-G28 set is complete for the stated objectives.

## 7. Corrections made by this review

All in the investigations; the ADR and backlog needed only one count fix:

1. **`investigations/abstraction-propagation-gap-audit.md` §2.3** — had the live/dead attribution **inverted**: it called the `Shared.Core/Ignixa` copies "the live ones" and the standalone `Microsoft.Health.Fhir.Ignixa` project "dead weight." Verified reality: `Shared.Core.projitems:11` references the standalone project (it is the live implementation compiled into every Core assembly); only 3 of 10 files in the `Shared.Core/Ignixa` folder are compiled; the other 7 are the dead copies. Left uncorrected, this could have led US-23's implementer to delete the wrong copy. (The shim audit §3.5 and ADR G26/US-23 already had it right — the ADR resolved the contradiction correctly.) Corrected in place.
2. **`investigations/dual-provider-feature-flags.md` §8** — claimed the stale disk copies are "byte-identical today." Hash comparison shows `FhirPath/IgnixaCompiledFhirPath.cs` has already drifted (6 of 7 identical). Corrected in place; strengthens the delete-now recommendation.
3. **`user-stories.md` US-23** — "~8" duplicate files corrected to the exact count, 7.

Residual nits, noted but not worth edits: investigations cite `Api/Modules/ValidationModule.cs` for a file that lives in `Shared.Api/Modules/` (the namespace is `...Api.Modules`; line numbers are exact); the ADR cites `ServiceCollectionExtensions.cs:148-166` for a class spanning 148-167.

## 8. What this means for execution

- Start with US-3 (isolated, mechanical, unblocks validation) and US-1+US-2 (the flag + formatter fix, which must land together) — the review found nothing that changes Phase 1.
- Treat the ADR's "Ignixa serves zero HTTP traffic today" as the verified baseline: any performance or correctness claim about Ignixa on the HTTP path predating the G1 fix should be considered unvalidated, and the mandatory E2E re-validation pass in the ADR's Consequences is not optional.
- The line-number citations across the ADR and backlog were spot-checked extensively and found exact; future readers can navigate by them with confidence.
