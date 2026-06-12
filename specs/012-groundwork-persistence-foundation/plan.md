# Implementation Plan: Groundwork Persistence Foundation

**Branch**: `codex/groundwork-persistence-foundation` | **Date**: 2026-06-10 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/012-groundwork-persistence-foundation/spec.md`

## Summary

Define Groundwork as a generic provider-neutral persistence framework and rewrite the Persistence vNext roadmap as Groundwork-first execution slices validated by optional application host examples. This slice creates the product boundary, package map, storage intent model, minimum manifest vocabulary, roadmap mapping, and host integration bridge responsibilities before any Groundwork implementation projects are added.

## Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`) for future implementation; G0 is documentation and planning only

**Primary Dependencies**: None for G0; future core packages should stay limited to BCL and permitted `Microsoft.Extensions.*` abstractions where needed

**Storage**: No storage implementation in G0; future slices cover schema history, portable document envelopes, declared indexes, and provider materialization

**Testing**: Spec/checklist validation for G0; future slices use xUnit unit tests plus provider integration contract tests

**Target Platform**: Standalone .NET library packages consumed by .NET applications

**Project Type**: Generic persistence framework plus application integration bridge

**Performance Goals**: G0 defines storage intent gates only; storage that needs stronger runtime behavior remains benchmark-gated until G8

**Constraints**: Groundwork generic packages must not reference host-specific packages or host-specific domain concepts; provider packages remain provider-suffixed; application hosts consume Groundwork through an explicit bridge

**Scale/Scope**: Product definition and roadmap planning for G0; no source projects or provider code in this slice

## Constitution Check

| Gate | Status | Note |
|---|---|---|
| Framework §2.1 three-layer separation | PASS | Groundwork uses its own `.Core`, helper/planning, and provider package boundaries instead of adding contracts to host-specific packages. |
| Framework §2.9 persistence invariants provider-neutral | PASS | The manifest vocabulary defines invariants independently of EF Core, MongoDB, or relational mechanisms. |
| Framework §2.10 CQS at persistence boundary | PASS | G0 preserves separate command/query concerns for future stores and avoids combined mutate/query repository contracts. |
| Framework §2.20 provider module decomposition | PASS | Provider packages are named `Groundwork.<Provider>` and remain separate from generic kernel packages. |
| Runtime separation guardrail | PASS | Host integration examples do not let runtime stores depend on design-side persistence. |
| Runtime persistence guardrail | PASS | Runtime continuation state remains benchmark-gated and does not become a generic document-store assumption. |
| Framework §2.23 tests | PASS | G0 has no implementation logic; future implementation slices define unit and provider contract tests. |

No justified violations.

## Project Structure

### Documentation (this feature)

```text
specs/012-groundwork-persistence-foundation/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── groundwork-boundary.md
│   └── roadmap-slices.md
└── checklists/
    └── requirements.md
```

### Source Code (future slices, not created in G0)

```text
src/Groundwork/Core/
src/Groundwork/Documents/
src/Groundwork/Relational/
src/Groundwork/Sqlite/
src/Groundwork/SqlServer/
src/Groundwork/PostgreSql/
src/Groundwork/MongoDb/
src/Groundwork/Hosting/
tests/Groundwork/
tests/Groundwork/Groundwork.Hosting.Tests/
```

**Structure Decision**: G0 creates only planning artifacts. Generic Groundwork projects live under `src/Groundwork`. Optional host-facing integration lives under `src/Groundwork/Hosting` and maps host module/storage concerns onto Groundwork manifests without leaking host-specific names into generic Groundwork packages.

## Complexity Tracking

No constitution violations or complexity exceptions are introduced in G0.
