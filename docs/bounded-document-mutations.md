# Bounded document mutations

Bounded document mutations are named lifecycle operations over existing bounded-query plans. They
are not a general update/delete language. A manifest fixes both the predicate shape and the only
allowed effect; a runtime caller supplies an operation identity and the values for the remaining
declared predicates.

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
discriminator, field identifiers, operations, and handler identity. Transition source values and
the target value are compiled plan data and cannot be supplied or changed at runtime.

`PhysicalMutationDocumentStore` resolves the mutation name and validates its complete closed shape
before provider I/O. There is no `IQueryable`, expression-tree, caller-supplied assignment, optional
full scan, or caller scope field. Scope is resolved from the document-store session and the compiled
route.

## Execution contract

A provider mutation handler must:

1. certify the exact compiled mutation and derive its supported operations/actions from executable
   handler capabilities;
2. evaluate every range, status, and relationship predicate on the server through the compiled
   physical query plan;
3. select stable physical document identities before changing any row;
4. change the canonical document, primary projections, linked projections/index rows, and durable
   operation ledger in one transaction;
5. return the exact selected identity count;
6. persist a canonical request fingerprint with that count;
7. return `Replayed` with the original count for an identical retry, and throw
   `BoundedMutationOperationConflictException` when an operation identity is reused for a different
   request.

The request fingerprint includes the provider-neutral compiled route and predicate semantics,
storage unit, route-derived scope, mutation identity, fixed action, and canonicalized clauses.
Provider implementation name/version and handler identity are deliberately excluded: a retry after
a rolling provider upgrade must resolve the same durable operation. Clause order, comparison order
inside an OR clause, and IN-value order do not change the fingerprint.

The durable ledger key is manifest identity, provider name, storage unit, route-derived scope, and
operation identity. Provider version is retained only as completion evidence, not as operation
identity, so a version upgrade replays the original exact result instead of executing the mutation
again.

## Relational reference execution

The relational handler renders the selector with the same SQL builder used by bounded reads. It
inserts the selected document identities into a transaction-local table, applies the fixed delete or
transition to primary and linked storage, and records the operation outcome before commit. This
identity boundary prevents a linked-row change from changing which primary rows belong to the same
operation.

SQLite is the reference provider slice. Its runtime binds a compiled indexed plan with `INDEXED BY`,
updates canonical JSON with `json_set`, starts direct-connection mutation transactions at SQLite's
immediate writer boundary, and provisions
`groundwork_document_mutation_operations` alongside the physical-schema infrastructure. Conformance
coverage includes exact delete/transition identity joins for shared-document, dedicated-document,
and physical-entity storage, compound equality/range selection, scope isolation, concurrent retry
across session factories and direct connections, deterministic conflict, cancellation/failure and
cleanup-failure rollback, rolling-upgrade acknowledgement-loss recovery, and `EXPLAIN QUERY PLAN`
index use.

SQL Server, PostgreSQL, and MongoDB must implement the same provider-neutral contract and
conformance scenarios through their native transaction, mutation, ledger, and explain facilities.
