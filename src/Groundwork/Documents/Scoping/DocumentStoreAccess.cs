using Groundwork.Core.Scoping;

namespace Groundwork.Documents.Scoping;

/// <summary>A deliberate capability supplied when privileged storage access is acquired.</summary>
public sealed record PrivilegedStorageAccess
{
    public PrivilegedStorageAccess(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    public string Reason { get; }
}

public enum DocumentStoreAccessKind
{
    Scoped,
    Global,
    PrivilegedScoped,
    PrivilegedGlobal,
    PrivilegedAcrossScopes
}

/// <summary>
/// Scope ownership bound to a document store session. Ordinary callers must choose scoped or
/// explicitly global access. Cross-scope access exists only through a distinct privileged capability.
/// </summary>
public sealed record DocumentStoreAccess
{
    internal const string GlobalStorageKey = "__groundwork_global__";

    private DocumentStoreAccess(
        DocumentStoreAccessKind kind,
        StorageScope? scope,
        PrivilegedStorageAccess? privilege)
    {
        Kind = kind;
        Scope = scope;
        Privilege = privilege;
    }

    public DocumentStoreAccessKind Kind { get; }

    public StorageScope? Scope { get; }

    public PrivilegedStorageAccess? Privilege { get; }

    public bool IsPrivileged => Privilege is not null;

    public static DocumentStoreAccess Scoped(StorageScope scope) =>
        new(DocumentStoreAccessKind.Scoped, scope ?? throw new ArgumentNullException(nameof(scope)), null);

    public static DocumentStoreAccess Global { get; } =
        new(DocumentStoreAccessKind.Global, null, null);

    public static DocumentStoreAccess PrivilegedScoped(PrivilegedStorageAccess privilege, StorageScope scope) =>
        new(
            DocumentStoreAccessKind.PrivilegedScoped,
            scope ?? throw new ArgumentNullException(nameof(scope)),
            privilege ?? throw new ArgumentNullException(nameof(privilege)));

    public static DocumentStoreAccess PrivilegedGlobal(PrivilegedStorageAccess privilege) =>
        new(DocumentStoreAccessKind.PrivilegedGlobal, null, privilege ?? throw new ArgumentNullException(nameof(privilege)));

    public static DocumentStoreAccess PrivilegedAcrossScopes(PrivilegedStorageAccess privilege) =>
        new(DocumentStoreAccessKind.PrivilegedAcrossScopes, null, privilege ?? throw new ArgumentNullException(nameof(privilege)));
}

public enum StorageScopeOperation
{
    Save,
    Load,
    Delete,
    Query,
    Mutate,
    BeginUnitOfWork
}

public enum StorageScopeRejectionReason
{
    ScopedAccessRequired,
    GlobalAccessRequired,
    TargetScopeRequired,
    MixedUnitOfWorkPolicy,
}

/// <summary>Low-cardinality evidence for rejected access; deliberately contains no scope value.</summary>
public sealed record StorageScopeAccessRejection(
    StorageScopeOperation Operation,
    StorageScopePolicy RequiredPolicy,
    StorageScopeRejectionReason Reason);

/// <summary>
/// Evidence emitted once when a privileged store session is acquired. Metrics should use only
/// <see cref="AccessKind"/>; the operator-supplied reason belongs in logs/traces, never metric labels.
/// </summary>
public sealed record PrivilegedStorageSessionAudit(DocumentStoreAccessKind AccessKind, string Reason);

public interface IStorageScopeObserver
{
    void PrivilegedSessionAcquired(PrivilegedStorageSessionAudit audit);

    void ScopeAccessRejected(StorageScopeAccessRejection rejection);
}

public sealed class NullStorageScopeObserver : IStorageScopeObserver
{
    public static NullStorageScopeObserver Instance { get; } = new();

    private NullStorageScopeObserver()
    {
    }

    public void PrivilegedSessionAcquired(PrivilegedStorageSessionAudit audit)
    {
    }

    public void ScopeAccessRejected(StorageScopeAccessRejection rejection)
    {
    }
}

public sealed class InvalidStorageScopeAccessException : InvalidOperationException
{
    public InvalidStorageScopeAccessException(StorageScopeAccessRejection rejection, string message)
        : base(message) => Rejection = rejection;

    public StorageScopeAccessRejection Rejection { get; }
}
