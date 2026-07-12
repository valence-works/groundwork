using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Groundwork.Documents.Scoping;

internal static class StorageScopeInstrumentation
{
    private static readonly Meter Meter = new("Groundwork.Documents.StorageScope");
    private static readonly ActivitySource Activities = new("Groundwork.Documents.StorageScope");
    private static readonly Counter<long> PrivilegedSessions =
        Meter.CreateCounter<long>("groundwork.document_store.privileged_sessions");
    private static readonly Counter<long> RejectedAccess =
        Meter.CreateCounter<long>("groundwork.document_store.scope_rejections");

    public static void RecordPrivilegedAcquisition(PrivilegedStorageSessionAudit audit)
    {
        PrivilegedSessions.Add(1, new KeyValuePair<string, object?>("access.kind", audit.AccessKind.ToString()));
        using var activity = Activities.StartActivity("groundwork.document_store.privileged_session");
        activity?.SetTag("groundwork.storage.access.kind", audit.AccessKind.ToString());
        activity?.SetTag("groundwork.storage.privilege.reason", audit.Reason);
    }

    public static void RecordRejection(StorageScopeAccessRejection rejection)
    {
        TagList tags = new()
        {
            { "operation", rejection.Operation.ToString() },
            { "required.policy", rejection.RequiredPolicy.ToString() },
            { "reason", rejection.Reason.ToString() }
        };
        RejectedAccess.Add(1, tags);
        using var activity = Activities.StartActivity("groundwork.document_store.scope_rejected");
        activity?.SetTag("groundwork.storage.operation", rejection.Operation.ToString());
        activity?.SetTag("groundwork.storage.required_policy", rejection.RequiredPolicy.ToString());
        activity?.SetTag("groundwork.storage.rejection_reason", rejection.Reason.ToString());
    }
}
