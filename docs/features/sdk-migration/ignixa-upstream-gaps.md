# Ignixa Upstream Gap Tracker

Running tab of Ignixa SDK gaps that block full parity in `FhirSdkMode.Ignixa`/`Hybrid`,
tracked as issues in [brendankowitz/ignixa-fhir](https://github.com/brendankowitz/ignixa-fhir).

This is narrower than [ADR-2607](adr-2607-ignixa-merge-readiness.md)'s gap register: ADR-2607
covers fhir-server-side migration work generally; this tracks only the subset that are genuine
upstream SDK defects — things Ignixa itself should fix, not things fhir-server needs to build.
Update this table whenever a new upstream gap is found or an existing issue is resolved.

| # | Issue | Gap | fhir-server workaround it blocks removing | Status |
|---|---|---|---|---|
| 1 | [ignixa-fhir#320](https://github.com/brendankowitz/ignixa-fhir/issues/320) | `ValidationDepth.Compatibility` doesn't run profile-tier checks (`CodeSystemPropertyTypeCheck`, `ValueSetIncludeSystemCheck`, `ValueSetFilterCheck`) — `ValidationSchema.Validate` gates them to `Depth == Full` exactly, not `>= Spec`-style. `Compatibility` is meant to provide Firely-equivalent validation behavior, so this is a real gap, not intentional scoping. | `IgnixaResourceValidator`'s `ConformanceResourceTypes` exclusion list keeps `CodeSystem` and `ValueSet` permanently routed to the Firely fallback validator (see [provider-map.md](provider-map.md), US-11 / Phase 3 Task 3) | Open |
