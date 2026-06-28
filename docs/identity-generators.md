# Identity generators

Groundwork generates a handful of identifiers itself — outbox/inbox **message ids** and **lease
tokens** in the operational stores. These land in indexed/PK columns, so the id format matters: a
random GUID is not sortable and fragments the B-tree it lives in.

The `IIdentityGenerator` abstraction (in `Groundwork.Core`, namespace
`Groundwork.Core.Identity`) lets a consumer pick the id format. It is a single method:

```csharp
public interface IIdentityGenerator
{
    string Generate();
}
```

There is **no DI container** and no `services.Add…` registration. Like the operational clock
(`IOperationalClock? clock = null`), a generator is an optional constructor parameter with a sensible
default — pass one in to override it.

## The catalog

The time-ordered generators take a `TimeProvider` for their time source (so tests can drive them
deterministically); `GuidIdentityGenerator` takes no parameters. All except `Guid` are sortable by
ordinal string comparison.

| Generator | Output | Length | Sortable | Coordination | When to use |
| --- | --- | --- | --- | --- | --- |
| `ShortIdentityGenerator` *(default)* | Base62 | 11 chars | yes (to the ms) | none | Default. Compact, time-ordered, no setup. |
| `UuidV7IdentityGenerator` | lowercase hex (`"N"`) | 32 chars | yes | none | Full UUID width with chronological ordering. |
| `SnowflakeIdentityGenerator` | Base62 | 11 chars | yes (strictly increasing per worker) | a unique worker id per instance | Strict monotonic ordering / explicit worker partitioning. |
| `GuidIdentityGenerator` | lowercase hex (`"N"`) | 32 chars | **no** | none | Parity / callers that don't need ordering. Not recommended for indexed keys. |

### `ShortIdentityGenerator` (default)

A 64-bit value: a 42-bit millisecond timestamp (relative to the epoch `2020-01-01T00:00:00Z`, valid
until ~2159) in the high bits and 22 random bits in the low bits, `Base62`-encoded to 11 characters.
Sortable to the millisecond, no coordination required. Under extreme per-millisecond throughput the 22
random bits carry a small collision probability; use the Snowflake generator if you need a hard
guarantee.

### `UuidV7IdentityGenerator`

`Guid.CreateVersion7(...)` rendered as 32 lowercase hex chars. 128 bits, effectively collision-free,
and sortable by its canonical string because the high bits are a millisecond timestamp.

### `SnowflakeIdentityGenerator`

A short 64-bit Snowflake, `Base62`-encoded to 11 chars. Layout (high → low): 41-bit ms timestamp
(from a configurable epoch, default `2020-01-01Z`) | 10-bit worker id (0–1023) | 12-bit sequence.

It holds monotonic state (guarded by a `System.Threading.Lock`): within the same millisecond it
increments the 12-bit sequence; on sequence overflow it spins to the next millisecond; if the clock
moves backwards it throws `InvalidOperationException`. Ids are strictly increasing per worker, and
distinct workers never collide. **Create one instance per worker and reuse it** — the instance *is*
the coordination point.

```csharp
var generator = new SnowflakeIdentityGenerator(
    TimeProvider.System,
    new SnowflakeIdentityGeneratorOptions { WorkerId = 1 }); // 0–1023, must be unique per instance
```

`WorkerId` outside `[0, 1023]` throws `ArgumentOutOfRangeException`.

### `Base62`

`ShortIdentityGenerator` and `SnowflakeIdentityGenerator` share one internal `Base62` encoder.
Alphabet `0-9 A-Z a-z` (ascending ASCII), fixed 11-char width. Fixed width + ascending alphabet means
ordinal string order equals numeric order; 11 chars is the smallest width that holds the full `ulong`
range.

### Convenience factory

```csharp
var gen = GroundworkIdentityGenerators.Create(IdentityGeneratorKind.Short, TimeProvider.System);
```

`IdentityGeneratorKind.Snowflake` requires `SnowflakeIdentityGeneratorOptions`.

## Passing a generator into the operational stores

The store constructors accept an optional `IIdentityGenerator? identityGenerator = null`,
defaulting to `new ShortIdentityGenerator()`. Existing call sites keep compiling unchanged and pick up
the short, sortable default automatically.

```csharp
// Default (ShortIdentityGenerator):
var store = new SqliteOperationalStore(connection);

// Override with UUID v7:
var store = new SqliteOperationalStore(connection, identityGenerator: new UuidV7IdentityGenerator());

// Snowflake with an explicit worker id:
var store = new SqliteOperationalStore(
    connection,
    identityGenerator: new SnowflakeIdentityGenerator(
        TimeProvider.System,
        new SnowflakeIdentityGeneratorOptions { WorkerId = 1 }));
```

The same parameter is available on `RelationalOperationalStore`.

> Document ids are unaffected — `IDocumentStore.SaveAsync` receives the id from the caller. This
> abstraction only governs ids Groundwork generates itself, plus the reusable catalog other code can
> opt into.

## Format compatibility with Elsa

This catalog deliberately mirrors Elsa's `Elsa.Primitives.Identity` catalog (`IIdentityGenerator`,
`ShortIdentityGenerator`, `UuidV7IdentityGenerator`, `SnowflakeIdentityGenerator`,
`GuidIdentityGenerator`) — same Base62 alphabet and the same bit layouts — so identifiers produced by
either repository are format-compatible.

Because the two are independent copies, that compatibility is a convention rather than an automatic
invariant. It is pinned by golden-value tests (`IdentityFormatCompatibilityTests`) that exist with
**identical literals** in both repos. If you change an epoch, bit split, or alphabet here, update
Elsa's copy and its golden test to match.
