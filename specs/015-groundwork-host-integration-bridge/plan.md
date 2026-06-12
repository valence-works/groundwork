# Implementation Plan: Groundwork Host Integration Bridge

**Branch**: `codex/groundwork-host-integration-bridge` | **Date**: 2026-06-10 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/015-groundwork-host-integration-bridge/spec.md`

## Summary

Implement the G3 host integration bridge MVP. Add `Groundwork.Hosting` as an opt-in integration package that registers Groundwork manifests, provider adapters, startup materialization, and diagnostics. Validate the bridge with a SQLite adapter in tests and a Secrets-like manifest without introducing host-specific references into generic Groundwork packages.

## Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`)

**Primary Dependencies**: `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`, `Groundwork.Core`, `Groundwork.Documents`

**Storage**: Bridge is storage-agnostic; tests use `Groundwork.Sqlite` with in-memory SQLite

**Testing**: xUnit integration tests for bridge registration, startup task behavior, diagnostics, SQLite materialization, and dependency boundaries

**Target Platform**: Standalone Groundwork host-integration package for .NET application composition

**Project Type**: host integration library plus tests

**Performance Goals**: Startup materialization correctness only; no runtime benchmark goals in G3

**Constraints**: Bridge is opt-in; bridge must not reference provider-specific packages; Groundwork projects must not reference host-specific projects; no existing EF persistence path may be replaced

**Scale/Scope**: One bridge project, one test project, solution entries, Spec Kit artifacts, program-goal pointer updates

## Constitution Check

| Gate | Status | Note |
|---|---|---|
| Framework §2.1 three-layer separation | PASS | Generic Groundwork remains separate; host-specific integration lives under `Groundwork.Hosting`. |
| Framework §2.9 persistence invariants provider-neutral | PASS | Bridge registers manifests/providers but does not redefine storage invariants. |
| Framework §2.20 provider module decomposition | PASS | Provider-specific SQLite adapter is test-local for G3; bridge depends only on provider abstraction. |
| Runtime migration guardrail | PASS | G3 is opt-in and does not migrate workflow runtime hot paths. |
| Framework §2.23 tests | PASS | Bridge registration, startup, diagnostics, and boundaries are covered with focused tests. |

No justified violations.

## Project Structure

### Documentation (this feature)

```text
specs/015-groundwork-host-integration-bridge/
├── spec.md
├── plan.md
├── tasks.md
├── quickstart.md
└── checklists/
    └── requirements.md
```

### Source Code

```text
src/Groundwork/Hosting/
├── Groundwork.Hosting.csproj
├── Diagnostics/
│   ├── GroundworkPersistenceDiagnostics.cs
│   └── GroundworkPersistenceDiagnosticSnapshot.cs
├── Extensions/
│   └── ServiceCollectionExtensions.cs
├── Options/
│   └── GroundworkPersistenceOptions.cs
├── Providers/
│   └── IGroundworkPersistenceProvider.cs
├── Secrets/
│   └── SecretsGroundworkManifestFactory.cs
├── Tasks/
│   └── MaterializeGroundworkStartupTask.cs
└── GroundworkPersistenceFeature.cs

tests/Groundwork/Groundwork.Hosting.Tests/
├── Groundwork.Hosting.Tests.csproj
├── GroundworkPersistenceFeatureTests.cs
├── MaterializeGroundworkStartupTaskTests.cs
└── GroundworkBridgeBoundaryTests.cs
```

**Structure Decision**: G3 adds only the optional host integration bridge project under `src/Groundwork/Hosting`. SQLite remains a Groundwork provider dependency used by tests, not by the bridge package.

## Complexity Tracking

No constitution violations or complexity exceptions are introduced in G3.
