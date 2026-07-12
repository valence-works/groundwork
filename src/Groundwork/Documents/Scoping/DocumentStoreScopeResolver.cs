using Groundwork.Core.Manifests;
using Groundwork.Core.Scoping;

namespace Groundwork.Documents.Scoping;

public sealed record DocumentScopeSelection(string? StorageKey, StorageScope? Scope, bool AcrossScopes)
{
    public static DocumentScopeSelection Global { get; } =
        new(DocumentStoreAccess.GlobalStorageKey, null, false);
}

/// <summary>Central executable scope-policy handler shared by every document provider.</summary>
public static class DocumentStoreScopeResolver
{
    public static StorageScope? ReadScope(string storageKey) =>
        storageKey == DocumentStoreAccess.GlobalStorageKey ? null : new StorageScope(storageKey);

    public static void ObserveAcquisition(DocumentStoreAccess access, IStorageScopeObserver observer)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(observer);
        if (access.IsPrivileged)
        {
            var audit = new PrivilegedStorageSessionAudit(access.Kind, access.Privilege!.Reason);
            StorageScopeInstrumentation.RecordPrivilegedAcquisition(audit);
            observer.PrivilegedSessionAcquired(audit);
        }
    }

    public static DocumentScopeSelection Resolve(
        StorageUnit unit,
        DocumentStoreAccess access,
        StorageScopeOperation operation,
        IStorageScopeObserver observer,
        bool allowAcrossScopes = false)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(observer);

        var policy = unit.Tenancy?.Kind switch
        {
            TenancyKind.Global => StorageScopePolicy.Global,
            TenancyKind.Scoped => StorageScopePolicy.Scoped,
            _ => throw Reject(
                observer,
                operation,
                StorageScopePolicy.Scoped,
                StorageScopeRejectionReason.ScopedAccessRequired,
                $"Storage unit '{unit.Identity.Value}' has no executable scope policy.")
        };

        if (policy == StorageScopePolicy.Global)
        {
            if (access.Kind is DocumentStoreAccessKind.Global or DocumentStoreAccessKind.PrivilegedGlobal)
                return DocumentScopeSelection.Global;

            throw Reject(
                observer,
                operation,
                policy,
                StorageScopeRejectionReason.GlobalAccessRequired,
                $"Storage unit '{unit.Identity.Value}' requires an explicitly global document-store session.");
        }

        if (access.Scope is { } scope &&
            access.Kind is DocumentStoreAccessKind.Scoped or DocumentStoreAccessKind.PrivilegedScoped)
        {
            return new DocumentScopeSelection(scope.Value, scope, false);
        }

        if (access.Kind == DocumentStoreAccessKind.PrivilegedAcrossScopes)
        {
            if (allowAcrossScopes)
                return new DocumentScopeSelection(null, null, true);

            throw Reject(
                observer,
                operation,
                policy,
                StorageScopeRejectionReason.TargetScopeRequired,
                $"Operation '{operation}' on scoped storage unit '{unit.Identity.Value}' requires a target scope even in a privileged session.");
        }

        throw Reject(
            observer,
            operation,
            policy,
            StorageScopeRejectionReason.ScopedAccessRequired,
            $"Storage unit '{unit.Identity.Value}' requires a scoped document-store session.");
    }

    public static InvalidStorageScopeAccessException RejectMixedUnitOfWork(
        IStorageScopeObserver observer,
        StorageScopePolicy policy) =>
        Reject(
            observer,
            StorageScopeOperation.BeginUnitOfWork,
            policy,
            StorageScopeRejectionReason.MixedUnitOfWorkPolicy,
            "A document unit of work cannot mix global and scoped storage units.");

    private static InvalidStorageScopeAccessException Reject(
        IStorageScopeObserver observer,
        StorageScopeOperation operation,
        StorageScopePolicy policy,
        StorageScopeRejectionReason reason,
        string message)
    {
        var rejection = new StorageScopeAccessRejection(operation, policy, reason);
        StorageScopeInstrumentation.RecordRejection(rejection);
        observer.ScopeAccessRejected(rejection);
        return new InvalidStorageScopeAccessException(rejection, message);
    }
}
