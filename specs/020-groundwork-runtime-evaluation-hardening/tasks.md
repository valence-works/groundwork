# Tasks: Groundwork Runtime Evaluation And Hardening

## Phase 1: Setup

- [x] T001 Create G8 specification and implementation plan in `specs/020-groundwork-runtime-evaluation-hardening/`
- [x] T002 Update Speckit and program-goal pointers for G8

## Phase 2: Runtime Evaluation Surface

- [x] T003 Add runtime store candidate/evaluation models in `src/Groundwork/Hosting/RuntimeEvaluation/`
- [x] T004 Add runtime store evaluator in `src/Groundwork/Hosting/RuntimeEvaluation/GroundworkRuntimeStoreEvaluator.cs`
- [x] T005 Add runtime store evaluator tests in `tests/Groundwork/Groundwork.Hosting.Tests/GroundworkRuntimeStoreEvaluatorTests.cs`

## Phase 3: Decision Report

- [x] T006 Add runtime evaluation report in `docs/reports/groundwork-runtime-evaluation.md`
- [x] T007 Verify report and evaluator matrix agree

## Phase 4: Validation

- [x] T008 Run `dotnet test tests/Groundwork/Groundwork.Hosting.Tests/Groundwork.Hosting.Tests.csproj`
- [x] T009 Run `dotnet test Groundwork.slnx --no-restore`
