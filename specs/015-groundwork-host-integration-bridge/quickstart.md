# Quickstart: Groundwork Host Integration Bridge

## Register The Bridge

1. Add `Groundwork.Hosting`.
2. Register the bridge with one or more manifests:

```csharp
services.AddGroundworkHosting(options =>
{
    options.Manifests.Add(SecretsGroundworkManifestFactory.Create());
});
```

3. Register one or more `IGroundworkPersistenceProvider` adapters in the application composition.
4. Run application host startup tasks. The bridge startup task materializes the configured manifests against registered providers when `MaterializeOnStartup` is `true`.

## Inspect Diagnostics

Resolve `GroundworkPersistenceDiagnostics` and request a snapshot. The snapshot reports:

- registered manifest identities
- registered provider identities
- materialization records and statuses

## Validation Commands

```bash
dotnet test tests/Groundwork/Groundwork.Hosting.Tests/Groundwork.Hosting.Tests.csproj
dotnet test tests/Groundwork/Groundwork.Tests/Groundwork.Tests.csproj
dotnet test Groundwork.slnx --no-restore
```
