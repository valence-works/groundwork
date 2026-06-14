# Groundwork.Modules.Inbox — a third-party capability module

This sample proves Groundwork's **open/closed** capability system: it adds a brand-new persistence
semantic — an **idempotent inbox / exactly-once consumer** — entirely from outside, **without editing
`Groundwork.Core`**.

It is the natural complement to the built-in outbox: where the outbox guarantees at-least-once
*delivery*, the inbox guarantees at-most-once *processing* by deduplicating redelivered messages.

## What it contributes

| Layer | Project | Contents |
|---|---|---|
| Capability + contract | `Groundwork.Modules.Inbox` | `InboxCapabilities.IdempotentConsumer` (`community.inbox.idempotent-consumer`), `InboxModule : IGroundworkModule`, `IInboxStore`, schema DDL. References only `Groundwork.Core`. |
| Provider impl | `Groundwork.Modules.Inbox.Sqlite` | `SqliteInboxStore` on the reusable `Groundwork.Provider.Relational` toolkit; advertises the capability. |
| Proof | `Groundwork.Modules.Inbox.Tests` | Dedup behaviour + capability-fit derivation. |

## The contract

```csharp
public interface IInboxStore
{
    Task<InboxAdmission> TryAdmitAsync(string consumer, string messageKey, CancellationToken ct = default);
    Task MarkProcessedAsync(string consumer, string messageKey, CancellationToken ct = default);
    Task<bool> IsProcessedAsync(string consumer, string messageKey, CancellationToken ct = default);
}
// InboxAdmission = Admitted | Duplicate
```

`TryAdmitAsync` returns `Admitted` the first time a `(consumer, messageKey)` pair is seen and
`Duplicate` on every redelivery — implemented with `INSERT ... ON CONFLICT DO NOTHING`.

## Wiring the module

```csharp
var (registry, evidencePolicy) = new GroundworkModuleCatalog()
    .Add(new InboxModule())
    .Build();

var validator = new ProviderCapabilityValidator(registry);
ProviderFit fit = validator.Evaluate(manifest, providerReport, evidencePolicy);
```

A provider advertises support with
`report.WithCapabilities(InboxCapabilities.IdempotentConsumer)`. A manifest unit declares the need
with `StorageIntent.Operational(rationale, descriptor, InboxCapabilities.IdempotentConsumer)`.

## Why this proves open/closed

- The module lives under `samples/`, outside the core dependency-boundary allow-list, so it cannot
  edit or be referenced by core.
- The existing `ProviderCapabilityValidator` derives `Supported` / `Unsupported` for the custom
  capability exactly as for built-ins.
- A core-only validator (default registry) rejects the unknown capability with `GW-CAP-014`,
  demonstrating that the registry — not a hardcoded enum — is the source of truth.

See `docs/reports/groundwork-open-closed-capabilities.md` for the full design.
