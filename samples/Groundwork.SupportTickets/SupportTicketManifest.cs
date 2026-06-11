using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Workloads;

namespace Groundwork.SupportTickets;

public static class SupportTicketManifest
{
    public const string DocumentKind = "supportTicket";
    public const string SchemaVersion = "1.0.0";
    public const string ByTicketNumber = "by-ticket-number";
    public const string ByCustomer = "by-customer";
    public const string ByStatus = "by-status";
    public const string ByAssignee = "by-assignee";
    public const string ByPriority = "by-priority";

    public static StorageManifest Create() =>
        new(
            new StorageManifestIdentity("support-tickets"),
            new StorageManifestOwner("groundwork.sample.support"),
            new StorageManifestVersion(SchemaVersion),
            [
                new StorageUnit(
                    new StorageUnitIdentity(DocumentKind),
                    "Support ticket",
                    new WorkloadClassification(WorkloadFamily.RuntimeDefinedBusinessData, WorkloadCandidateCategory.GroundworkDefault),
                    LifecyclePolicy.Mutable,
                    IdentityPolicy.StringId(),
                    TenancyPolicy.None,
                    ConcurrencyPolicy.Optimistic(),
                    SerializationPolicy.Json(),
                    Indexes(),
                    Queries(),
                    PhysicalizationPolicy.Portable)
            ],
            new HashSet<string> { "schema-history", "optimistic-concurrency" },
            []);

    private static IReadOnlyList<IndexDeclaration> Indexes() =>
    [
        Keyword(ByTicketNumber, "ticketNumber", isUnique: true),
        Keyword(ByCustomer, "customerId"),
        Keyword(ByStatus, "status"),
        Keyword(ByAssignee, "assigneeId"),
        Keyword(ByPriority, "priority")
    ];

    private static IReadOnlyList<PortableQueryDeclaration> Queries() =>
    [
        Query("find-by-ticket-number", ByTicketNumber),
        Query("list-by-customer", ByCustomer, QuerySortSupport.Both, QueryPagingSupport.Offset),
        Query("list-by-status", ByStatus, QuerySortSupport.Both, QueryPagingSupport.Offset),
        Query("list-by-assignee", ByAssignee, QuerySortSupport.Both, QueryPagingSupport.Offset),
        Query("list-by-priority", ByPriority, QuerySortSupport.Both, QueryPagingSupport.Offset)
    ];

    private static IndexDeclaration Keyword(string identity, string field, bool isUnique = false) =>
        new(
            identity,
            [new IndexField(field)],
            IndexValueKind.Keyword,
            isUnique,
            true,
            MissingValueBehavior.Excluded,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal });

    private static PortableQueryDeclaration Query(
        string identity,
        string indexName,
        QuerySortSupport sort = QuerySortSupport.None,
        QueryPagingSupport paging = QueryPagingSupport.None) =>
        new(
            identity,
            indexName,
            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
            sort,
            paging);
}
