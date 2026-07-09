# Feature: SDK Migration - Firely to Ignixa

## Problem Statement

The Microsoft FHIR Server currently depends heavily on the Firely SDK (v5.11.4) for:
- FHIR resource model types (version-specific assemblies)
- JSON/XML serialization/deserialization
- FhirPath expression compilation and evaluation
- Profile validation
- Search parameter extraction
- Terminology operations

This architecture requires **separate assemblies per FHIR version** (STU3, R4, R4B, R5), leading to:
- Complex build configurations with conditional compilation
- Deployment complexity with multiple DLLs
- Code duplication across version-specific projects
- Maintenance overhead for version-specific bug fixes

## Desired Outcome

A unified FHIR server using the Ignixa SDK that:
- Handles all FHIR versions (STU3, R4, R4B, R5, R6) in a single assembly
- Uses HTTP header-based version negotiation (FHIR spec compliant)
- Provides high-performance JSON serialization without Firely overhead
- Maintains full FHIR compliance for all operations

## Current State

| Metric | Value |
|--------|-------|
| Firely SDK version | 5.11.4 |
| Files using `Hl7.Fhir.*` | 400+ |
| Total Firely usages | 602+ |
| Version-specific projects | 16 (4 versions x 4 layers) |

## Investigations

| Investigation | Status | Verdict |
|--------------|--------|---------|
| [Complete Firely Replacement with Ignixa](investigations/complete-ignixa-replacement.md) | In Progress | Pending |
| [Ignixa FHIRPath Provider Migration](investigations/ignixa-fhirpath-provider.md) | In Progress | Recommended |
| [Dual Provider Feature Flags](investigations/dual-provider-feature-flags.md) | Merged (ADR-2607) | Single `SdkMode` enum flag (Hybrid/Firely/Ignixa); force-Firely is cheap, force-Ignixa blocked on formatter-selection defect below |
| [Abstraction Propagation Gap Audit](investigations/abstraction-propagation-gap-audit.md) | Merged (ADR-2607) | Abstractions exist but propagation stopped at first consumer; 4 layers (bundle pipeline, validation, XML, ad-hoc FHIRPath) have no abstraction at all |
| [Shim Minimization Audit](investigations/shim-minimization-audit.md) | Merged (ADR-2607) | 27 shims cataloged (S1-S27); headline defect confirmed independently by two other investigations (see below) |
| [Validation SDK Dependency](investigations/validation-sdk-dependency.md) | Merged (ADR-2607) | Non-blocker for objective 2; `Ignixa` package pin (0.0.163) predates PR #310's validation work, which ships from 0.6.7 — version bump is a P0 prerequisite |
| [XML Pipeline Ignixa Adoption](investigations/xml-pipeline-ignixa-adoption.md) | Merged (ADR-2607) | Defer as explicit objective-5 carve-out; Ignixa has no production XML support upstream, and the SupportsXml=false 406 mechanism already covers a strict Ignixa-only deployment |

**Cross-cutting critical finding (confirmed independently by 3 of 5 investigations above):** the Ignixa MVC JSON formatters are registered but never selected at runtime. `IgnixaFormatterConfiguration` (`IConfigureOptions<MvcOptions>`) inserts them at index 0, but `FormatterConfiguration` (`IPostConfigureOptions`, `FhirModule.cs:146`) runs afterward and re-inserts the legacy Firely formatters ahead of them. Since the legacy formatter claims every `[FromBody]` type controllers use, **the HTTP boundary is 100% Firely today** — the existing test-readiness report's claim that Ignixa formatters were "E2E validated" does not hold. This is the top-priority item feeding the ADR.

## Related ADRs

- [ADR-2607: Ignixa Merge Readiness — SdkMode Flag, Gap Register, and Deferral Policy](adr-2607-ignixa-merge-readiness.md) (Proposed) — synthesizes the five 2026-07-08 investigations; implementation backlog in [user-stories.md](user-stories.md)

## Notes

- E2E tests are excluded from this migration scope
- The ignixa/ folder contains reference implementation examples
- Ignixa NuGet packages are available at https://www.nuget.org/packages?q=Ignixa
