# Bounded document mutations

Bounded document mutations are named lifecycle operations over existing bounded-query plans. They
are not a general update/delete language. A manifest fixes both the predicate shape and the only
allowed effect; a runtime caller supplies an operation identity and the values for the remaining
declared predicates.

## Point document operations

`Save`, `Delete`, and `Load` by a known document identity are bounded point operations, not
scale-bearing selection routes. They therefore do not require a generic document-mutation plan
inspector. When a workflow first selects or retains a potentially large set, native plan evidence
belongs to that preceding bounded query, capacity check, or diagnostic-record statistics route;
the subsequent identity-addressed writes are covered by the selected set's contract.

```csharp
var physicalStorage = new StorageUnitPhysicalStorage(
    StorageUnitProvisioningMode.Declared,
    policy,
    logicalIndexes,
    boundedQueries,
    boundedMutations:
    [
        new BoundedMutationDeclaration(
            "revoke-pending",
            "by-authorization-and-status",
            BoundedMutationAction.Transition("status", ["pending"], "revoked")),
        new BoundedMutationDeclaration(
            "prune-expired",
            "by-authorization-and-expiration",
            BoundedMutationAction.Delete())
    ]);
```

`PhysicalMutationPlanCompiler` resolves each declaration through the existing physical-query plan
compiler. A mutation is executable only when its predicate is scale-bearing and has a physical
index. The compiled plan therefore carries the exact provider, route, object, index, scope,
discriminator, field identifiers, operations, and handler identity. Transition paths must resolve
to document content (canonical JSON, a content projection, or a provider-native content field);
envelope and linked-relationship fields are immutable through this action, while delete predicates
may still select by those intrinsic fields. Transition source values and the target value are
compiled plan data and cannot be supplied or changed at runtime.

`PhysicalMutationDocumentStore` resolves the mutation name and validates its complete closed shape
before provider I/O. There is no `IQueryable`, expression-tree, caller-supplied assignment, optional
full scan, or caller scope field. Scope is resolved from the document-store session and the compiled
route. Provider runtimes also bind the exact manifest content and route fingerprint already owned by
the store; a same-identity manifest with altered declarations or a route from another store is
rejected before database access.

## Execution contract

A provider mutation handler must:

1. certify the exact compiled mutation and derive its supported operations/actions from executable
   handler capabilities;
2. evaluate every range, status, and relationship predicate on the server through the compiled
   physical query plan;
3. establish one transaction-stable mutation set, either by provider-local identity materialization
   or by using the same provider-native set-based selector for every affected physical object;
4. change the canonical document, primary projections, linked projections/index rows, and durable
   operation ledger in one transaction;
5. return the exact primary physical affected count and reject mismatched linked/projection counts;
6. persist a canonical request fingerprint with that count;
7. return `Replayed` with the original count for an identical retry, and throw
   `BoundedMutationOperationConflictException` when an operation identity is reused for a different
   request.

The request fingerprint includes the provider-neutral compiled route and predicate semantics,
storage unit, route-derived scope, mutation identity, fixed action, and canonicalized clauses.
Provider implementation name/version and handler identity are deliberately excluded: a retry after
a rolling provider upgrade must resolve the same durable operation. Clause order, comparison order
inside an OR clause, and IN-value order do not change the fingerprint.
Document-identity predicate values pass through the predicate plan's identity binding before
selection and fingerprinting, so case-policy-equivalent spellings share one replay fingerprint.
Operation identities themselves remain ordinal and are never projected through document identity
policy.

The durable ledger key is manifest identity, provider name, storage unit, route-derived scope, and
operation identity. Provider version is retained only as completion evidence, not as operation
identity, so a version upgrade replays the original exact result instead of executing the mutation
again.

Relational providers retain all five ledger identity values for exact collision verification while
using provider-generated SHA-256 keys for the primary key. SQL Server hashes each unbounded
`nvarchar(max)` value through `varbinary(max)`. PostgreSQL hashes the exact UTF-8 representation
through a validated provider-owned immutable function and stored generated `bytea` columns. This
keeps lookup keys bounded without imposing a length limit on operation identities.

## Relational reference execution

The relational handler renders the selector with the same SQL builder used by bounded reads. It
inserts the selected document identities into a transaction-local table, applies the fixed delete or
transition to primary and linked storage, and records the operation outcome before commit. This
identity boundary prevents a linked-row change from changing which primary rows belong to the same
operation.

SQLite, SQL Server, and PostgreSQL use the same internal relational mutation runtime and
provider-bound public runtime factories. SQLite binds indexed selectors with `INDEXED BY`, changes
canonical JSON with `json_set`, and starts direct-connection transactions at the immediate writer
boundary. SQL Server uses indexed query sources, transaction-owned `sp_getapplock` operation locks,
session-local selection tables, `JSON_MODIFY`, and retained-original plus persisted-hash identity
joins. Its transition parameters preserve numbers and booleans as native JSON scalars while
string, keyword, date-time, and GUID values remain JSON strings. PostgreSQL uses transaction-scoped
advisory operation locks, `ON COMMIT DROP` selection tables, native `text[]` JSON paths with
`jsonb_set`, and exact retained identity joins.

All three providers provision and validate `groundwork_document_mutation_operations` alongside the
physical-schema infrastructure. SQL Server and PostgreSQL conformance covers exact transition and
delete joins for shared-document, dedicated-document, and physical-entity storage, compound
relationship/range selection, scope isolation, same-operation concurrency, deterministic conflict,
cancellation/failure rollback, restart and rolling-upgrade acknowledgement-loss replay, and native
plan evidence for the declared selector index. SQLite additionally covers direct-connection writer
serialization and cleanup-failure preservation.

## MongoDB set-based execution

MongoDB stores the physical canonical document as addressable BSON and serializes standard JSON on
read. Provider-owned, route-scoped typed mutation mirrors are written to primary and linked
documents during every save. Each compiled mutation contributes an additive binding-fence definition,
while mutations sharing a logical index contribute one deduplicated selector definition. Both flow
through the ordinary physical-schema plan under the same lease, operation ledger, applied snapshot,
validation, and CLI lifecycle as portable schema work. Binding definitions install strict collection
validators; selector definitions create exact compound indexes over discriminator, scope, and declared
logical-index paths on both collections and backfill pre-existing documents once per selector. Backfill
writes are document-incarnation and version fenced, are
safe to replay after an unpublished attempt, and validate the final primary and linked mirror state
before the target is published. The validators require an exact immutable-binding fence on every
relevant primary and linked write. A host still running the pre-mutation model is therefore rejected by
MongoDB during the validation/publication gap and throughout rolling coexistence instead of creating
selector-invisible documents. Additive mutation declarations compose their validator rules on shared
collections, and live schema validation proves each rule is still active. A bounded mutation can
therefore execute one hinted `UpdateMany` or
`DeleteMany` against each physical object without loading document identities or issuing per-document
writes. Transitions update canonical BSON, native BSON, typed mirrors, ordinary projections, and
versions in the same transaction as the durable operation outcome.

MongoDB's canonical boundary retains JSON number lexemes that exceed BSON's native numeric envelope
through a provider-owned raw-number tag and emits them as standard JSON numbers on read. Original
JSON whitespace is not retained. Replica-set or sharded-cluster transaction support is required and
is checked before ledger or document I/O. Native `explain` evidence must select the provider-owned
exact primary and linked mutation indexes rather than a collection scan. Explain evidence is derived
from the same immutable binding used by schema materialization, capability certification, and runtime
execution.
