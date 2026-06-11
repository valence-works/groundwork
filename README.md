# Groundwork

Groundwork is a provider-neutral persistence foundation for .NET applications. Modules describe storage intent through manifests, and providers translate those manifests into concrete relational or document database structures.

This repository contains the standalone Groundwork library extracted from the Elsa foundation workspace.

## Projects

- `Groundwork.Core`: manifests, workload classification, provider capability checks, validation, materialization concepts, and physicalization projection rules.
- `Groundwork.Documents`: portable document-store contracts and document planning.
- `Groundwork.Relational`: relational planning and shared relational document-store infrastructure.
- `Groundwork.Sqlite`: SQLite materialization and document-store provider.
- `Groundwork.SqlServer`: SQL Server materialization and document-store provider.
- `Groundwork.PostgreSql`: PostgreSQL materialization and document-store provider.
- `Groundwork.MongoDb`: MongoDB materialization and document-store provider.

## Requirements

- .NET SDK 10.0 or newer.
- Docker for provider tests that use container-backed databases.

## Build And Test

```bash
dotnet test tests/Groundwork/Groundwork.Tests/Groundwork.Tests.csproj
dotnet test tests/Groundwork/Groundwork.Sqlite.Tests/Groundwork.Sqlite.Tests.csproj
dotnet test samples/Groundwork.SupportTickets.Tests/Groundwork.SupportTickets.Tests.csproj
```

Provider integration suites can be run separately when Docker-backed databases are available:

```bash
dotnet test tests/Groundwork/Groundwork.MongoDb.Tests/Groundwork.MongoDb.Tests.csproj
dotnet test tests/Groundwork/Groundwork.RelationalProviders.Tests/Groundwork.RelationalProviders.Tests.csproj
```

## Sample

`samples/Groundwork.SupportTickets` demonstrates a small support ticket domain on top of `Groundwork.Sqlite`.

The sample:

- defines a `supportTicket` manifest with unique ticket numbers and queryable customer, status, assignee, and priority indexes;
- materializes the SQLite schema;
- creates and loads tickets through `IDocumentStore`;
- queries by declared indexes;
- updates tickets with optimistic concurrency;
- surfaces duplicate ticket numbers as write conflicts.

Run it with:

```bash
dotnet run --project samples/Groundwork.SupportTickets/Groundwork.SupportTickets.csproj
```

The historical specs and Groundwork-focused planning notes are kept under `specs/` and `docs/`.
