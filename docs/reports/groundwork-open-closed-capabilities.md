# Open/Closed Capabilities — Extending Groundwork Without Editing Core

> Status: implemented. This note documents the capability system that lets third parties add new
> persistence semantics (a new `CapabilityId`, contract, and provider implementation) **without
> editing `Groundwork.Core`**, and the migration away from the closed `StorageRequirement` enum.

## Problem

Groundwork could already **compose** a custom pattern out of existing primitives, but a pattern that
needed a **new persistence semantic** could not be added from the outside. The capability vocabulary
was closed in three places:

1. `StorageRequirement` — a hardcoded `enum` (the closed seam).
2. `ProviderCapabilityReport` — `IReadOnlySet<StorageRequirement>` for supported/evidenced sets.
3. `ProviderCapabilityValidator` + `WorkloadEvidencePolicy` — enum-driven matching, plus a hardcoded
   manifest `RequiredCapabilities` switch.

A third party therefore could not introduce a new capability + contract + provider implementation
without changing core. The fix makes the capability system **data-driven and registry-based**.

## Architecture

```
Groundwork.Core/Capabilities/
  CapabilityId               readonly record struct over a namespaced string (vendor.area.name)
  CapabilityDescriptor       Id, DisplayName, Description, EvidenceGatedByDefault, OwningModule
  WellKnownCapabilities      the 11 built-in operational/query capabilities as constants + descriptors
  ICapabilityRegistry        lookup by id; enumerate descriptors
  CapabilityRegistry         Default = built-ins; CreateBuilder() pre-seeds built-ins
  IGroundworkModule          Name + RegisterCapabilities(builder)
  GroundworkModuleCatalog    compose modules -> (registry, WorkloadEvidencePolicy)
  ProviderCapabilityReport   SupportedCapabilities / EvidencedCapabilities are IReadOnlySet<CapabilityId>
  ProviderCapabilityValidator registry-driven; unknown id -> GW-CAP-014
  WorkloadEvidencePolicy     derived from descriptor defaults (FromRegistry)

Groundwork.Provider.Relational   reusable relational toolkit (connection gate, executor, sequence)
                                  extracted from Operational.Relational so any module reuses it.
```

### `CapabilityId`

A namespaced string of the form `vendor.area.name`, validated by
`^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$` (lowercase, `[a-z0-9-]` segments, at
least two dot-separated segments). `groundwork.*` is reserved for built-ins; every module owns its
own namespace, which prevents collisions. The struct converts implicitly to `string` and exposes a
`Namespace` property.

### How each closed seam opened

| Closed before | Open now |
|---|---|
| `StorageRequirement` enum | `CapabilityId` string + `CapabilityDescriptor`; built-ins = `WellKnownCapabilities` |
| `ProviderCapabilityReport` enum sets | `IReadOnlySet<CapabilityId>` sets (`SupportedCapabilities` / `EvidencedCapabilities`) |
| Validator enum match | registry-driven id match; unregistered id → `GW-CAP-014` |
| `WorkloadEvidencePolicy` fixed set | composed from descriptor `EvidenceGatedByDefault` + module contributions |

The `ProviderCapabilityValidator` remains the **single compatibility authority**: `Evaluate` derives
`Supported / RequiresEvidence(reasons) / Unsupported(missing ids)` for custom capabilities exactly as
for built-ins. `Validate` additionally emits `GW-CAP-014` when a unit requires a capability that is
not present in the active registry (i.e. no module registered it).

## Module authoring contract

A third party ships, entirely outside core:

1. **Capability descriptors** — `CapabilityId` constants and an `IGroundworkModule` that registers
   them (carrying each descriptor's default evidence gating).
2. **Contract surface** — their own store interface(s) + DTOs, mirroring `IWorkQueueStore`.
3. **Provider implementation** — built on `Groundwork.Provider.Relational` (or any store), which
   **advertises** the new `CapabilityId`s in its `ProviderCapabilityReport` (via `WithCapabilities`).
4. **Manifest** — storage units declare the new `CapabilityId`s as requirements through
   `StorageIntent.Operational(...)`.

Wiring a module into the validator:

```csharp
var (registry, evidencePolicy) = new GroundworkModuleCatalog()
    .Add(new InboxModule())
    .Build();

var validator = new ProviderCapabilityValidator(registry);
ProviderFit fit = validator.Evaluate(manifest, providerReport, evidencePolicy);
```

## Proof: the Inbox module (`samples/Groundwork.Modules.Inbox*`)

A worked, zero-core-edit example implementing an **idempotent inbox / exactly-once consumer** (the
natural complement to the outbox):

- `Groundwork.Modules.Inbox` — `InboxCapabilities.IdempotentConsumer`
  (`community.inbox.idempotent-consumer`), `InboxModule : IGroundworkModule`, the `IInboxStore`
  contract, and schema DDL. References only `Groundwork.Core`.
- `Groundwork.Modules.Inbox.Sqlite` — `SqliteInboxStore` implemented on
  `Groundwork.Provider.Relational` (dedup via `INSERT ... ON CONFLICT DO NOTHING`). Advertises the
  capability on its provider report.
- `Groundwork.Modules.Inbox.Tests` — dedup behaviour plus the capability-fit proof: the inbox unit is
  `Supported` on a provider that advertises the capability, `Unsupported` on one that does not, and a
  core-only validator (default registry) rejects it with `GW-CAP-014`.

Because the module lives under `samples/` — outside the core dependency-boundary allow-list — "no
core edits" is structurally obvious: `GroundworkDependencyBoundaryTests` would fail if the module
tried to reference back into the closed core projects improperly.

## Migration from `StorageRequirement`

- `StorageRequirement.X` → `WellKnownCapabilities.X` (e.g. `AtomicClaim`, `AtomicCommit`,
  `RangeQuery`).
- `ProviderCapabilityReport.SupportedStorageRequirements` / `EvidencedStorageRequirements` →
  `SupportedCapabilities` / `EvidencedCapabilities` (both `IReadOnlySet<CapabilityId>`).
- `StorageIntent.Operational(rationale, descriptor, params CapabilityId[] requirements)` — the
  factory now takes capability ids.
- `WorkloadEvidencePolicy.Default` is `FromRegistry(CapabilityRegistry.Default)`; custom policies
  come from `GroundworkModuleCatalog.Build()`.

No backward compatibility shim is provided (per project direction); all manifests and tests were
migrated to the capability-id API.
