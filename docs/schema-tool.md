# Groundwork schema tool

Tracking: [Groundwork #49](https://github.com/valence-works/groundwork/issues/49).

`Groundwork.Tool` is the explicit CI/CD entry point for the provider-neutral physical-schema
protocol in [`Groundwork.Core.SchemaEvolution`](physical-schema-diffs.md). It loads an application's
manifest, compiles provider routes, reads the provider's durable #44 state, and can validate, plan,
report, or apply that target. It is not an application-startup migrator and Groundwork has no
automatic startup fallback.

## Install and version compatibility

Install the tool locally in the repository that owns the deployment pipeline:

```bash
dotnet new tool-manifest
dotnet tool install Groundwork.Tool --version 0.0.1
dotnet groundwork --version
```

Or install it globally:

```bash
dotnet tool install --global Groundwork.Tool --version 0.0.1
groundwork --version
```

Use the same Groundwork release version for `Groundwork.Tool`, `Groundwork.Core`, and the selected
provider package. A manifest-source assembly is loaded into the tool process and therefore must be
binary-compatible with that tool release. Pin the tool version in the tool manifest, update it with
the application's Groundwork packages, and run `validate` after every version change. Provider
identity and provider package version are part of every plan and its target fingerprint. `--version`
reports the exact installed package version, including a preview suffix, so pipelines can assert the
tool/package match before loading application code.

## Expose the provider-neutral manifest

The application assembly implements the Core-only `IPhysicalSchemaManifestSource`. It does not
choose a provider, accept a connection, or reference a provider SDK:

```csharp
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;

public sealed class ApplicationSchema : IPhysicalSchemaManifestSource
{
    public StorageManifest CreateManifest() => ApplicationManifests.Storage;

    public IPhysicalNamePolicy CreateNamePolicy() =>
        new DelegatePhysicalNamePolicy(context => $"app_{context.FeatureDefaultLogicalName}");
}
```

Build the application before invoking the tool. Supply `--manifest-type` when the assembly contains
more than one concrete source.

## Commands

The provider aliases are `sqlite`, `sqlserver`, `postgresql`, and `mongodb`.

```bash
dotnet groundwork validate \
  --manifest-assembly ./bin/Release/net10.0/Application.dll \
  --manifest-type ApplicationSchema \
  --provider sqlite \
  --connection-env GROUNDWORK_DEPLOYMENT_CONNECTION \
  --output json

dotnet groundwork validate \
  --manifest-assembly ./bin/Release/net10.0/Application.dll \
  --manifest-type ApplicationSchema \
  --provider sqlite \
  --offline \
  --output json

dotnet groundwork plan \
  --manifest-assembly ./bin/Release/net10.0/Application.dll \
  --manifest-type ApplicationSchema \
  --provider sqlite \
  --connection-env GROUNDWORK_DEPLOYMENT_CONNECTION \
  --output json

dotnet groundwork status \
  --manifest-assembly ./bin/Release/net10.0/Application.dll \
  --manifest-type ApplicationSchema \
  --provider sqlite \
  --connection-env GROUNDWORK_DEPLOYMENT_CONNECTION \
  --output json

dotnet groundwork apply \
  --manifest-assembly ./bin/Release/net10.0/Application.dll \
  --manifest-type ApplicationSchema \
  --provider sqlite \
  --connection-env GROUNDWORK_DEPLOYMENT_CONNECTION \
  --safe \
  --output json
```

Live `validate` is the default. It opens the selected provider, performs a point-in-time read of
durable applied state, and computes readiness without acquiring the application lock, creating
Groundwork infrastructure, recording evidence, or changing target objects. `validate --offline` is
the explicitly separate manifest/route compilation mode and accepts no connection input. Because a
live validation is intentionally non-locking, it validates the physical objects described by the
recorded applied routes before computing the desired diff. This detects drift in the applied schema
even when an additive or semantic target change is pending, without executing any pending operation.
Deployment still recomputes and authorizes the exact plan under the application lock.

`plan` and `status` read durable applied state under the provider/manifest exclusion lock; an
executor may establish its provider-owned lock/history infrastructure while doing so, but neither
command applies target operations. `apply` reads, authorizes, and executes one exact plan without
releasing that lock between those phases. Execution, retry, cancellation, acknowledgement-loss
recovery, and applied-state publication remain #44's `PhysicalSchemaApplication` protocol.

MongoDB requires `--database` unless the connection URI already contains the database name.
`--connection` is supported for non-interactive runners, but `--connection-env` is preferred because
command-line arguments can be visible in process listings. The environment-variable name and its
value are never emitted.

## Authorization

Safe application is explicit and refuses every operation carrying destructive or semantic evolution
metadata:

```bash
dotnet groundwork apply ... --safe
```

For an authorized plan, first retain the `planFingerprint` and exact destructive operation identities
from `plan --output json`, then bind approval to that plan:

```bash
dotnet groundwork apply ... \
  --expected-plan 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
  --allow-destructive create-physical-entity-storage:documents:example:0123456789abcdef \
  --allow-semantic reclassify-v2
```

`--allow-destructive` and `--allow-semantic` are repeatable and match exact identities. A stale
`--expected-plan` is rejected after the provider lock is acquired and before any target operation;
approval can never authorize a different plan observed after an unlocked preflight. Authorization
does not turn an unsupported transform into a supported one: a projected field declared
`SemanticMigrationRequired` remains the blocking `GW-SCHEMA-005` diagnostic until an authored
provider-neutral semantic migration exists.

## Stable output

`--output json` emits one compact JSON object followed by a newline. Schema version `1` contains:

- command, outcome, and live/offline inspection mode when validating;
- exact provider name/version and manifest target identity/version/fingerprint;
- deterministic plan fingerprint and previously applied target fingerprint;
- deterministically ordered resolved physical names;
- pending and applied operation identities, fingerprints, kinds, storage units, and subjects;
- exact destructive operation and semantic migration identities requiring authorization;
- blocking diagnostics; and
- `targetMutated`, which is true only when this invocation applied target work.

No timestamps, connection values, exception messages, stack traces, or user-controlled high-cardinality
telemetry tags are emitted. Human output carries the same summary in a stable line-oriented form.
The `Groundwork.SchemaTool` activity source emits only `groundwork.command`, `groundwork.provider`,
`groundwork.outcome`, and `groundwork.exit_code`.

## Pipeline exit codes

| Code | Name | Meaning |
|---:|---|---|
| `0` | success | Validation passed, no work is pending, or apply completed/reconciled successfully. |
| `2` | pending changes | `plan` or `status` found applicable pending operations. |
| `3` | validation failed | Blocking manifest, route, history, or physical-schema diagnostics exist. |
| `4` | authorization required | Safe apply found protected work, exact approval is incomplete, or the locked plan differs from `--expected-plan`. |
| `5` | invalid invocation | Required source, provider, connection, database, or option input is missing/invalid. |
| `10` | execution failed | Provider execution failed; details are deliberately suppressed from output. |
| `130` | cancelled | Caller cancellation or Ctrl+C stopped the command before unapplied state was published. |

For a plan gate, accept `0` or `2` and fail every other value. For a deployment gate, require apply
to return `0`:

```bash
set +e
dotnet groundwork plan ... --output json > groundwork-plan.json
code=$?
set -e
if [ "$code" -ne 0 ] && [ "$code" -ne 2 ]; then
  exit "$code"
fi

dotnet groundwork apply ... --safe --output json > groundwork-apply.json
```

Grant the deployment identity only the provider permissions required for the declared target plus
Groundwork's lock, operation-evidence, and applied-state infrastructure. Application-specific
orchestration, EF migrations, and implicit host-startup application remain outside the tool.
