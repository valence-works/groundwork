# Implementation Plan: Groundwork Runtime-Defined Entities

**Branch**: `codex/groundwork-runtime-entities` | **Date**: 2026-06-10 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/018-groundwork-runtime-entities/spec.md`

## Summary

Implement G6 by adding a small opt-in runtime entity surface to `Groundwork.Hosting`. Runtime entity definitions declare fields and indexes, a manifest factory maps them to Groundwork storage units, and a runtime entity store saves/loads/queries/deletes definitions and instances through `IDocumentStore`. Validate with SQLite-backed tests.

## Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`)

**Primary Dependencies**: Existing `Groundwork.Hosting`, `Groundwork.Core`, `Groundwork.Documents`, `Groundwork.Sqlite` in tests

**Storage**: Groundwork portable document storage

**Testing**: xUnit tests with SQLite in-memory provider

**Target Platform**: standalone Groundwork host-integration package

**Project Type**: host integration library plus tests

**Performance Goals**: Correctness only; physicalization/performance deferred to G7

**Constraints**: Runtime entity concepts remain in host integration bridge; generic Groundwork packages remain free of host-specific dependencies; no physical table per runtime entity

**Scale/Scope**: Runtime entity models, manifest factory, store wrapper, tests, Spec Kit artifacts, program-goal pointer updates

## Constitution Check

| Gate | Status | Note |
|---|---|---|
| Framework §2.1 three-layer separation | PASS | Runtime entity concepts live in host integration bridge, not generic Groundwork core. |
| Framework §2.9 persistence invariants provider-neutral | PASS | Runtime entity store delegates to `IDocumentStore`. |
| Framework §2.20 provider module decomposition | PASS | G6 adds no provider-specific package. |
| Runtime migration guardrail | PASS | Runtime hot paths remain out of scope. |
| Framework §2.23 tests | PASS | SQLite-backed tests cover manifest mapping and instance operations. |

No justified violations.

## Project Structure

### Documentation (this feature)

```text
specs/018-groundwork-runtime-entities/
├── spec.md
├── plan.md
├── tasks.md
├── quickstart.md
└── checklists/
    └── requirements.md
```

### Source Code

```text
src/Groundwork/Hosting/RuntimeEntities/
├── RuntimeEntityDefinition.cs
├── RuntimeEntityManifestFactory.cs
└── GroundworkRuntimeEntityStore.cs

tests/Groundwork/Groundwork.Hosting.Tests/
├── RuntimeEntityManifestFactoryTests.cs
└── GroundworkRuntimeEntityStoreTests.cs
```

**Structure Decision**: G6 extends the existing host integration bridge package and test project. It does not create a generic Groundwork runtime-entity package.

## Complexity Tracking

No constitution violations or complexity exceptions are introduced in G6.
