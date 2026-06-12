# Feature Specification: Groundwork Persistence Foundation

**Feature Branch**: `codex/groundwork-persistence-foundation`

**Created**: 2026-06-10

**Status**: Draft

**Input**: User description: "Define Groundwork as a provider-neutral persistence framework with manifests, planners, portable document storage, and a host integration example."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Define Groundwork As A Generic Product (Priority: P1)

An architect can read the Groundwork specification and understand what belongs in the generic persistence framework, what belongs in application-specific integrations, and why a host application is only an example consumer rather than part of the generic product language.

**Why this priority**: Without a clean product boundary, provider and host integration work will leak application concepts into reusable contracts and make standalone reuse harder.

**Independent Test**: Review the specification and planning artifacts and verify that Groundwork concepts are named generically while host-specific validation is isolated to the integration boundary.

**Acceptance Scenarios**:

1. **Given** the Groundwork specification, **When** an architect reviews the framework purpose and scope, **Then** they can distinguish Groundwork kernel responsibilities from host integration responsibilities.
2. **Given** a proposed concept such as workflow definition, activity catalog, secret, or bookmark, **When** it is evaluated against the Groundwork boundary, **Then** the concept is classified as an application-side use of generic storage vocabulary rather than a generic Groundwork primitive.
3. **Given** optional host integrations, **When** the package map is reviewed, **Then** generic Groundwork projects remain reusable without renaming host-specific types.

---

### User Story 2 - Classify Storage Intent Before Implementation (Priority: P2)

A persistence designer can classify each planned storage use case by storage intent before choosing the portable document store, relational planning, physicalization, or a specialized provider contract.

**Why this priority**: Treating all persistence as portable document storage would overextend the portable document store and risk applying it to runtime queues, logs, or checkpoint paths that need different guarantees.

**Independent Test**: Take the roadmap's sample candidates and classify each into portable document, benchmark-gated, or specialized-provider intent with explicit behavioral requirements where needed.

**Acceptance Scenarios**:

1. **Given** a metadata/configuration store, **When** it is classified, **Then** the portable document store is an acceptable default unless provider capabilities fail.
2. **Given** a runtime checkpoint state store, **When** it is classified, **Then** the plan marks it as benchmark-gated and not automatically covered by the portable document default.
3. **Given** storage that needs queue, log, or outbox behavior, **When** it is classified, **Then** the plan treats it as specialized unless later evidence proves Groundwork can satisfy those requirements.

---

### User Story 3 - Rewrite The Roadmap In Groundwork Terms (Priority: P3)

A delivery planner can take the original Persistence vNext roadmap and execute it as Groundwork-first slices validated by application host examples, while preserving the original staged proof points and provider order.

**Why this priority**: The roadmap already has useful provider and hardening sequencing, but it must be framed so application host examples validate Groundwork without defining the product language.

**Independent Test**: Compare the original roadmap with the Groundwork execution plan and verify that every original slice has a Groundwork-first equivalent, a host integration validation point, or an explicit deferral.

**Acceptance Scenarios**:

1. **Given** the original S1 core manifest and planner slice, **When** it is rewritten, **Then** the result targets Groundwork core manifests, provider capabilities, relational planning, document planning, and schema history.
2. **Given** the original provider slices, **When** they are rewritten, **Then** provider packages remain Groundwork-owned and application hosts consume them through an integration bridge.
3. **Given** the original runtime evaluation slice, **When** it is rewritten, **Then** the result produces a go/no-go decision for workflow runtime stores rather than assuming runtime migration.

### Edge Cases

- If a feature needs application-specific terminology to be understandable, the terminology stays in the application integration layer and maps onto generic Groundwork vocabulary.
- If storage can fit the document store only after provider-specific physicalization, the default classification must state that requirement instead of presenting the portable store as sufficient.
- If a provider cannot support a declared manifest capability, the planning surface must require a clear startup/materialization failure or an explicit fallback decision.
- If a host integration requires application-specific APIs in generic packages, the package boundary is considered wrong and must be revised before implementation.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The specification MUST define Groundwork as a generic provider-neutral persistence framework, independent of host-specific domain concepts.
- **FR-002**: The specification MUST define the boundary between Groundwork kernel responsibilities and application integration responsibilities.
- **FR-003**: The specification MUST identify a package and namespace structure suitable for a standalone Groundwork repository.
- **FR-004**: The specification MUST define a storage intent model that separates portable document storage from benchmark-gated and specialized-provider storage.
- **FR-005**: The specification MUST state which storage intents can use Groundwork defaults and which require evidence or provider-specific contracts.
- **FR-006**: The specification MUST identify the minimum manifest vocabulary needed before provider implementation begins.
- **FR-007**: The specification MUST require that manifests express storage intent and behavioral requirements rather than provider-specific database shape.
- **FR-008**: The specification MUST define how the original Persistence vNext roadmap maps to Groundwork-first execution slices.
- **FR-009**: The specification MUST preserve a host application as an example validation consumer through a clearly named host integration bridge.
- **FR-010**: The specification MUST require provider capability reporting and materialization diagnostics as first-class planning concerns.
- **FR-011**: The specification MUST require schema/materialization history for applied plans.
- **FR-012**: The specification MUST require that unindexed or unsupported portable queries fail clearly rather than silently degrading to scans.
- **FR-013**: The specification MUST require that runtime continuation state and operational streams receive explicit go/no-go evaluation before any generic-store migration.
- **FR-014**: The specification MUST avoid introducing host-specific type names, workflow concepts, or module names into generic Groundwork concepts.
- **FR-015**: The specification MUST define acceptance criteria for the G0 product-definition slice before G1 implementation starts.

### Key Entities *(include if feature involves data)*

- **Groundwork Kernel**: The reusable product layer that owns generic persistence vocabulary, manifest validation, planning, capability reporting, document storage contracts, schema history, and provider diagnostics.
- **Application Integration Layer**: A consumer-specific bridge that discovers application declarations, maps application concepts to Groundwork manifests, registers providers, and exposes application diagnostics.
- **Storage Manifest**: A provider-neutral declaration of storage intent, behavioral requirements, indexes, lifecycle, concurrency expectations, serialization expectations, schema version, and materialization policy.
- **Storage Unit**: A named durable unit inside a manifest, such as a document kind, catalog item, projection, runtime state group, or specialized operational surface.
- **Storage Intent**: The declared portability gate and behavioral requirements that determine whether portable document storage, benchmark evidence, or specialized providers are required.
- **Provider Capability Report**: A provider-owned statement of supported manifest features, unsupported features, warnings, fallbacks, and materialization readiness.
- **Materialization Plan**: A provider-specific plan produced from provider-neutral storage intent and used to preview, apply, and record physical storage changes.
- **Host Integration Bridge**: The application host-side integration that validates Groundwork against real application host example stores while keeping Groundwork free of host-specific domain names.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A reviewer can classify at least ten roadmap storage candidates into Groundwork default, benchmark-gated, or specialized-provider categories without needing extra terminology.
- **SC-002**: The Groundwork package map contains zero host-prefixed generic packages and identifies all host-facing packages separately.
- **SC-003**: Every original Persistence vNext roadmap slice S1-S8 has a corresponding Groundwork execution slice, host integration validation slice, or explicit deferral note.
- **SC-004**: The G0 artifacts contain no unresolved `[NEEDS CLARIFICATION]` markers.
- **SC-005**: The G0 artifacts define enough manifest vocabulary for G1 planning to start without re-opening the product boundary.
- **SC-006**: The G0 artifacts include at least one explicit rule preventing runtime queues, execution logs, outbox records, and distributed locks from being silently treated as ordinary documents.

## Assumptions

- Groundwork is a standalone repository; host integrations must stay optional and separate from generic packages.
- The initial public product name and root namespace are `Groundwork`, subject to a later naming/trademark check before public package release.
- An application host can be an example consumer, but the generic framework must not reference host-specific packages or host-specific domain concepts.
- The existing Persistence vNext roadmap remains useful as sequencing evidence, but its slices will be renamed and scoped around Groundwork.
- Code implementation does not begin in G0; G0 produces product-definition and execution-planning artifacts that unblock G1.
- Runtime continuation state and operational streams remain benchmark-gated or specialized until evidence proves Groundwork can satisfy their required semantics.
