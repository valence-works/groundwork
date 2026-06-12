using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Workloads;

namespace Groundwork.SupportTickets;

public static class SupportTicketManifest
{
    public const string DocumentKind = "supportTicket";
    public const string CommentDocumentKind = "supportTicketComment";
    public const string SchemaVersion = "1.0.0";
    public const string ByTicketNumber = "by-ticket-number";
    public const string ByCustomer = "by-customer";
    public const string ByStatus = "by-status";
    public const string ByAssignee = "by-assignee";
    public const string ByPriority = "by-priority";
    public const string ByCommentTicket = "by-comment-ticket";
    public const string ByCommentAuthor = "by-comment-author";

    public static StorageManifest Create() => Create(PhysicalizationPolicy.Portable);

    public static StorageManifest Create(PhysicalizationPolicy physicalization) =>
        new(
            new StorageManifestIdentity("support-tickets"),
            new StorageManifestOwner("groundwork.sample.support"),
            new StorageManifestVersion(SchemaVersion),
            [
                TicketUnit(physicalization),
                CommentUnit(physicalization)
            ],
            new HashSet<string> { "schema-history", "optimistic-concurrency" },
            []);

    private static StorageUnit TicketUnit(PhysicalizationPolicy physicalization) =>
        new(
            new StorageUnitIdentity(DocumentKind),
            "Support ticket",
            new WorkloadClassification(WorkloadFamily.RuntimeDefinedBusinessData, WorkloadCandidateCategory.GroundworkDefault),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.None,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            TicketIndexes(),
            TicketQueries(),
            physicalization);

    private static StorageUnit CommentUnit(PhysicalizationPolicy physicalization) =>
        new(
            new StorageUnitIdentity(CommentDocumentKind),
            "Support ticket comment",
            new WorkloadClassification(WorkloadFamily.RuntimeDefinedBusinessData, WorkloadCandidateCategory.GroundworkDefault),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.None,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            CommentIndexes(),
            CommentQueries(),
            physicalization);

    private static IReadOnlyList<IndexDeclaration> TicketIndexes() =>
    [
        Keyword(ByTicketNumber, "ticketNumber", isUnique: true),
        Keyword(ByCustomer, "customerId"),
        Keyword(ByStatus, "status"),
        Keyword(ByAssignee, "assigneeId"),
        Keyword(ByPriority, "priority")
    ];

    private static IReadOnlyList<IndexDeclaration> CommentIndexes() =>
    [
        Keyword(ByCommentTicket, "ticketNumber"),
        Keyword(ByCommentAuthor, "authorId")
    ];

    private static IReadOnlyList<PortableQueryDeclaration> TicketQueries() =>
    [
        Query("find-by-ticket-number", ByTicketNumber),
        Query("list-by-customer", ByCustomer, QuerySortSupport.Both, QueryPagingSupport.Offset),
        Query("list-by-status", ByStatus, QuerySortSupport.Both, QueryPagingSupport.Offset),
        Query("list-by-assignee", ByAssignee, QuerySortSupport.Both, QueryPagingSupport.Offset),
        Query("list-by-priority", ByPriority, QuerySortSupport.Both, QueryPagingSupport.Offset)
    ];

    private static IReadOnlyList<PortableQueryDeclaration> CommentQueries() =>
    [
        Query("list-comments-by-ticket", ByCommentTicket, QuerySortSupport.Both, QueryPagingSupport.Offset),
        Query("list-comments-by-author", ByCommentAuthor, QuerySortSupport.Both, QueryPagingSupport.Offset)
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
