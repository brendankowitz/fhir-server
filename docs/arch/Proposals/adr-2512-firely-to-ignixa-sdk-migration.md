# ADR 2512: Migration from Firely SDK to Ignixa SDK
*Labels*: [Core](https://github.com/microsoft/fhir-server/labels/Area-Core) | [Dependencies](https://github.com/microsoft/fhir-server/labels/Area-Dependencies) | [FhirPath](https://github.com/microsoft/fhir-server/labels/Area-FhirPath) | [Validation](https://github.com/microsoft/fhir-server/labels/Area-Validation)

---

## Context

### Current State
The FHIR server currently depends on the Firely SDK (version 5.11.4) for core FHIR operations including:
- **FhirPath Expression Evaluation**: Compiling and executing search parameter expressions (~98 files affected)
- **Serialization/Deserialization**: JSON and XML parsing for FHIR resources (45+ files)
- **Element Model Operations**: ITypedElement, ISourceNode for resource navigation (30+ files)
- **Validation Framework**: Profile-based validation against Structure Definitions (15+ files)
- **Model Information**: POCO model metadata and type information (70+ files total)

### Package Dependencies
Current Firely SDK packages in use:
```xml
<Hl7FhirVersion>5.11.4</Hl7FhirVersion>
- Hl7.Fhir.Base
- Hl7.Fhir.STU3, Hl7.Fhir.R4, Hl7.Fhir.R4B, Hl7.Fhir.R5
- Hl7.Fhir.Validation.Legacy.* (STU3, R4, R4B, R5)
- Hl7.Fhir.Specification.Data.* (STU3, R4, R4B, R5)
- Hl7.Fhir.Specification.* (STU3, R4, R4B, R5)
```

### Problem Statement
The Ignixa SDK packages now provide a comprehensive alternative to the Firely SDK with:
- High-performance FHIR serialization (Ignixa.Serialization)
- Modern FhirPath engine (Ignixa.FhirPath)
- Profile-based validation (Ignixa.Validation)
- Search parameter indexing infrastructure (Ignixa.Search)
- Multi-version support (STU3, R4, R4B, R5, R6)
- Additional capabilities: FML, SQL on FHIR, NPM package management

### Strategic Rationale
Migrating to Ignixa provides:
1. **Modern Architecture**: Built on .NET 9.0 with contemporary patterns
2. **Enhanced Capabilities**: FML, SQL on FHIR, advanced package management
3. **Performance Potential**: High-performance serialization and optimized FhirPath evaluation
4. **Active Development**: Active maintenance and feature development
5. **Interoperability**: Bidirectional conversion support via Ignixa.Extensions.FirelySdk5

### Architecture Context
The FHIR server operates with:
- **Multi-Version Support**: Simultaneous support for STU3, R4, R4B, R5 using shared source code
- **High-Volume Operations**: Search indexing processes thousands of resources
- **Distributed Deployment**: Multiple instances with load balancing and auto-scaling
- **Complex Validation**: Profile-based validation with terminology services
- **Patch Operations**: FhirPath-based PATCH (RFC 6902) for resource updates

## Decision

We will **migrate from Firely SDK to Ignixa SDK** using a **3-phase incremental migration strategy** that minimizes risk and allows for validation at each stage.

### Migration Architecture

#### Phase 1: Foundation & Compatibility Layer (Zero Risk)
**Goal**: Establish Ignixa packages alongside Firely with zero breaking changes

**Actions**:
1. **Add Ignixa Packages** (keeping Firely packages temporarily):
   ```xml
   <!-- Core -->
   <PackageReference Include="Ignixa.Abstractions" Version="0.0.55" />
   <PackageReference Include="Ignixa.Specification" Version="0.0.55" />

   <!-- Interoperability Bridge -->
   <PackageReference Include="Ignixa.Extensions.FirelySdk5" Version="0.0.55" />

   <!-- Functionality -->
   <PackageReference Include="Ignixa.FhirPath" Version="0.0.55" />
   <PackageReference Include="Ignixa.Serialization" Version="0.0.55" />
   <PackageReference Include="Ignixa.Search" Version="0.0.55" />
   <PackageReference Include="Ignixa.Validation" Version="0.0.55" />
   ```

2. **Create Abstraction Interfaces** to decouple from SDK specifics:
   - `IFhirPathCompiler` - Abstraction over FhirPath compilation
   - `IFhirSerializer` - Abstraction over JSON/XML serialization
   - `IFhirValidator` - Abstraction over validation engine
   - `IElementModelConverter` - Abstraction over element model operations

   **Location**: `src/Microsoft.Health.Fhir.Core/Abstractions/`

3. **Implement Firely Adapters** (temporary wrappers):
   - `FirelyFhirPathCompiler` - Wraps existing `FhirPathCompiler`
   - `FirelyFhirSerializer` - Wraps `FhirJsonParser/Serializer`
   - `FirelyFhirValidator` - Wraps existing `ProfileValidator`

   **Location**: `src/Microsoft.Health.Fhir.Shared.Core/Adapters/`

4. **Update Dependency Injection** in `FhirModule.cs`:
   - Register abstraction interfaces with Firely implementations
   - Add feature flag `UseIgnixaEngine` for gradual rollout
   - Maintain backward compatibility

**Validation Gate**:
- ✅ All existing tests pass
- ✅ No functional changes
- ✅ Abstraction layer tested independently
- ✅ Code compiles with both Firely and Ignixa packages

#### Phase 2: Core Service Migration (Medium Risk)
**Goal**: Migrate FhirPath, Serialization, and Search to Ignixa while keeping Validation on Firely

**Critical Components to Migrate**:

1. **FhirPath Engine** (High Priority - affects search indexing)
   - **Files**:
     - `TypedElementSearchIndexer.cs` (322 lines)
     - `SearchParameterSupportResolver.cs`
     - `KnownFhirPaths.cs`
   - **Change**: Replace static `FhirPathCompiler` with injected `IFhirPathCompiler`
   - **Implementation**: Create `IgnixaFhirPathCompiler` using `Ignixa.FhirPath`

2. **Serialization Layer** (High Priority - affects 45+ files)
   - **Files**:
     - `FhirJsonInputFormatter.cs`
     - `FhirJsonOutputFormatter.cs`
     - `FhirXmlInputFormatter.cs`
     - `FhirXmlOutputFormatter.cs`
   - **Implementation**: Create `IgnixaFhirJsonSerializer` and `IgnixaFhirXmlSerializer`
   - **Bridge**: Use `Ignixa.Extensions.FirelySdk5` for bidirectional conversion during transition

3. **Model Information Provider** (Critical - central abstraction)
   - **Files**: `VersionSpecificModelInfoProvider.cs`
   - **Change**: Replace Firely's `PocoStructureDefinitionSummaryProvider` with `Ignixa.Specification`
   - **Methods**: `ToTypedElement()`, `GetEvaluationContext()`

4. **Search Indexing** (Medium Priority)
   - **Files**: Search value extractors (20+ files in `Search/Converters/`)
   - **Enhancement**: Leverage `Ignixa.Search` package for index value extraction

5. **FhirPath Patch Operations** (Medium Priority)
   - **Files**: Entire `Features/Resources/Patch/FhirPathPatch/` folder (10+ files)
   - **Change**: Migrate to Ignixa element model and FhirPath engine
   - **Components**: `FhirPathPatchBuilder`, operation classes (Add, Insert, Replace, Delete, Move, Upsert)

**Validation Gate**:
- ✅ FhirPath expressions evaluate identically (compare against test suite)
- ✅ Serialization round-trips produce identical output
- ✅ Search indexing produces same index values
- ✅ Patch operations work correctly
- ✅ Performance benchmarks show acceptable performance (≤10% regression)
- ✅ Integration tests pass with Ignixa flag enabled

#### Phase 3: Complete Migration & Cleanup (Low Risk)
**Goal**: Remove all Firely dependencies and optimize for Ignixa

**Actions**:

1. **Migrate Validation Framework** (Final major component)
   - **Files**: `ProfileValidator.cs` (145 lines), validation infrastructure (15+ files)
   - **Implementation**: Create `IgnixaProfileValidator` using `Ignixa.Validation`
   - **Challenges**: Terminology service integration, profile resolution, validation settings

2. **Remove Firely SDK Packages** from `Directory.Packages.props`:
   ```xml
   <!-- REMOVE all Hl7.Fhir.* packages -->
   ```

3. **Remove Adapter Layer**:
   - Delete `src/Microsoft.Health.Fhir.Shared.Core/Adapters/*`
   - Remove `Ignixa.Extensions.FirelySdk5` package
   - Direct registration of Ignixa implementations in DI

4. **Namespace Cleanup** (automated refactoring, 70+ production files, 40+ test files):
   - `Hl7.Fhir.ElementModel` → `Ignixa.Abstractions.ElementModel`
   - `Hl7.FhirPath` → `Ignixa.FhirPath`
   - `Hl7.Fhir.Serialization` → `Ignixa.Serialization`
   - `Hl7.Fhir.Specification` → `Ignixa.Specification`

5. **Optimize for Ignixa**:
   - Remove Firely compatibility shims
   - Optimize search indexing with Ignixa-native patterns
   - Review FhirPath expression caching

6. **Update Static Initialization**:
   - Remove: `FhirPathCompiler.DefaultSymbolTable.AddFhirExtensions()` (global state mutation)
   - Replace with: Ignixa's configuration-based extension registration

**Validation Gate**:
- ✅ All Firely packages removed from .csproj files
- ✅ No Firely namespaces in production code
- ✅ All tests pass (unit, integration, E2E)
- ✅ Performance benchmarks meet or exceed Firely baseline
- ✅ Validation produces identical results for test suite
- ✅ FhirPath evaluation matches reference implementation
- ✅ Serialization round-trips are perfect
- ✅ Memory usage and GC pressure acceptable
- ✅ Multi-version support (STU3, R4, R4B, R5) verified

### Migration Metrics

| Component | Files Affected | Lines of Code |
|-----------|---------------|---------------|
| FhirPath Engine | 20+ files | ~500 LOC |
| Serialization | 45+ files | ~1,200 LOC |
| Validation | 15+ files | ~800 LOC |
| Search Indexing | 25+ files | ~1,500 LOC |
| Patch Operations | 10+ files | ~600 LOC |
| **Total** | **70-90 files** | **~4,600 LOC** |

### Risk Mitigation Strategy
- **Phase 1**: Zero-risk (additive only, no behavior changes)
- **Phase 2**: Medium-risk (use feature flags, A/B testing, gradual rollout)
- **Phase 3**: Low-risk (cleanup after Phase 2 validation)

### Testing Strategy
- **Unit Tests**: Validate individual component migrations
- **Integration Tests**: Ensure end-to-end workflows work
- **Performance Tests**: Benchmark FhirPath, serialization, validation
- **Comparison Tests**: Firely vs Ignixa output comparison
- **Regression Tests**: Verify identical behavior for critical paths

## Status
**Proposed** - Awaiting approval and implementation

## Consequences

### Beneficial Effects

#### Modern Architecture and Performance
- **Contemporary Patterns**: Built on .NET 9.0 with modern C# features
- **Performance Potential**: High-performance serialization and optimized FhirPath evaluation
- **Memory Efficiency**: Potential improvements in GC pressure and memory allocation

#### Enhanced Capabilities
- **FHIR Mapping Language**: Access to FML parser and execution engine (`Ignixa.FhirMappingLanguage`)
- **SQL on FHIR**: Analytics query support (`Ignixa.SqlOnFhir`)
- **Package Management**: NPM package management for Implementation Guides (`Ignixa.PackageManagement`)
- **Multi-Version Support**: Support for R6 in addition to existing versions

#### Improved Maintainability
- **Active Development**: Active maintenance and feature development
- **SDK Alignment**: Alignment with modern FHIR SDK development
- **Clear Abstractions**: Better separation of concerns via abstraction layer
- **Reduced Global State**: Elimination of static mutations like `FhirPathCompiler.DefaultSymbolTable`

#### Risk-Controlled Migration
- **Incremental Approach**: 3-phase strategy minimizes blast radius
- **Feature Flags**: Gradual rollout with ability to rollback
- **Compatibility Bridge**: Ignixa.Extensions.FirelySdk5 enables coexistence during transition
- **Comprehensive Validation**: Multiple validation gates ensure correctness

### Adverse Effects

#### Migration Complexity and Cost
- **Large Code Surface**: 70-90 files requiring modification (~4,600 LOC)
- **Development Effort**: Significant engineering time across 3 phases
- **Testing Burden**: Extensive test suite updates and new comparison tests
- **Learning Curve**: Team must learn Ignixa SDK patterns and APIs

#### Temporary Dual Dependencies
- **Package Bloat**: Both Firely and Ignixa packages during Phase 1-2 (increased binary size)
- **Memory Overhead**: Two SDKs loaded simultaneously during transition
- **Complexity**: Adapter layer adds temporary indirection

#### Risk of Behavioral Differences
- **Serialization Variations**: Potential subtle differences in JSON/XML output formatting
- **FhirPath Semantics**: Edge cases may behave differently between engines
- **Validation Differences**: Profile validation may have subtle variations
- **Element Model**: Type conversion and navigation may differ

#### Performance Uncertainty
- **Benchmarking Required**: Unknown if Ignixa will meet or exceed Firely performance
- **Regression Risk**: Potential performance degradation in critical paths (≤10% acceptable)
- **Optimization Needed**: May require Ignixa-specific optimization after migration

#### Deployment and Operations Impact
- **Deployment Coordination**: Phased rollout requires careful coordination
- **Monitoring**: New metrics needed for Ignixa performance characteristics
- **Rollback Planning**: Contingency plans needed for each phase
- **Blue/Green Complexity**: Mixed SDK usage during rolling deployments

### Neutral Effects

#### API Compatibility Preserved
- **No Breaking Changes**: FHIR REST API surface remains identical
- **Client Impact**: Zero impact on API consumers
- **Resource Compatibility**: FHIR resources remain fully compatible

#### Development Workflow
- **Abstraction First**: Cleaner architecture emerges from abstraction layer
- **Testing Investment**: Enhanced test coverage benefits long-term quality
- **Documentation Updates**: Operational and development docs require updates

### Edge Cases and Mitigation Strategies

#### FhirPath Expression Compatibility
- **Scenario**: Ignixa FhirPath engine evaluates expressions differently than Firely
- **Impact**: Search results or validation outcomes may differ
- **Mitigation**: Comprehensive comparison test suite, side-by-side evaluation during Phase 2

#### Serialization Format Differences
- **Scenario**: Ignixa produces valid but differently formatted JSON/XML
- **Impact**: Clients relying on exact formatting may break
- **Mitigation**: Round-trip testing, format validation, client communication

#### Multi-Version Support Regression
- **Scenario**: Ignixa handles STU3/R4/R4B/R5 differently than Firely
- **Impact**: Version-specific functionality breaks
- **Mitigation**: Version-specific test suites, careful VersionSpecificModelInfoProvider migration

#### Performance Regression
- **Scenario**: Ignixa is slower than Firely for critical operations
- **Impact**: Increased latency, reduced throughput
- **Mitigation**: Performance benchmarking before Phase 2 completion, optimization work, rollback if >10% regression

#### Incomplete Ignixa Feature Parity
- **Scenario**: Ignixa lacks specific Firely SDK features
- **Impact**: Migration blocked or functionality lost
- **Mitigation**: Early validation of required features, engagement with Ignixa maintainers

### Success Criteria

#### Functional Correctness
- All existing tests pass without modification
- FhirPath evaluation produces identical results
- Serialization round-trips are lossless
- Validation outcomes match Firely behavior

#### Performance Targets
- Search indexing performance: ≤10% regression
- Serialization/deserialization: ≤10% regression
- FhirPath evaluation: ≤10% regression
- Memory usage: ≤20% increase acceptable

#### Code Quality
- Zero Firely dependencies in production code
- Clean abstraction layer with comprehensive interfaces
- No global state mutations
- Comprehensive test coverage (>90% for new abstractions)

#### Operational Excellence
- Successful phased rollout with zero downtime
- Monitoring dashboards updated for Ignixa metrics
- Documentation complete and accurate
- Team trained on Ignixa SDK

## References
- [Ignixa NuGet Packages](https://www.nuget.org/packages?q=Ignixa)
- [Firely SDK Documentation](https://docs.fire.ly/)
- FHIR Server Internal Documentation:
  - `docs/SearchArchitecture.md` - Search parameter indexing
  - `docs/arch/adr-2512-searchparameter-concurrency-management.md` - Related concurrency patterns
  - `docs/arch/adr-2505-eventual-consistency.md` - Search parameter management

## Implementation Plan

### Phase 1 Deliverables
- [ ] Ignixa packages added to Directory.Packages.props
- [ ] Abstraction interfaces created (IFhirPathCompiler, IFhirSerializer, IFhirValidator, IElementModelConverter)
- [ ] Firely adapter implementations created
- [ ] FhirModule updated with feature flag support
- [ ] All tests passing with abstraction layer

### Phase 2 Deliverables
- [ ] IgnixaFhirPathCompiler implemented and tested
- [ ] IgnixaFhirSerializer implemented and tested
- [ ] IgnixaModelInfoProvider implemented and tested
- [ ] TypedElementSearchIndexer migrated to use abstractions
- [ ] FhirPath patch operations migrated
- [ ] Feature flag enabled in test environments
- [ ] Performance benchmarks completed and acceptable
- [ ] Integration tests passing with Ignixa

### Phase 3 Deliverables
- [ ] IgnixaProfileValidator implemented and tested
- [ ] All Firely packages removed from Directory.Packages.props
- [ ] Adapter layer deleted
- [ ] Ignixa.Extensions.FirelySdk5 removed
- [ ] Namespace cleanup completed across all files
- [ ] Static initialization refactored
- [ ] All tests passing with Ignixa only
- [ ] Production deployment successful
- [ ] Performance meets or exceeds baseline

### Timeline Considerations
- **Phase 1**: Low complexity, establish foundation
- **Phase 2**: High complexity, core migration work
- **Phase 3**: Medium complexity, cleanup and optimization

Each phase must complete its validation gate before proceeding to the next phase.
