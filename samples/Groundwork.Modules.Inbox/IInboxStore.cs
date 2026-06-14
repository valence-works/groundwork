namespace Groundwork.Modules.Inbox;

/// <summary>Result of attempting to admit a message into the inbox.</summary>
public enum InboxAdmission
{
    /// <summary>First time this (consumer, message-key) was seen; the caller should process it.</summary>
    Admitted,

    /// <summary>Already admitted previously; the caller should skip processing (idempotency).</summary>
    Duplicate
}

/// <summary>
/// Idempotent-consumer inbox: deduplicates redelivered messages so a given (consumer, message-key)
/// is admitted — and therefore processed — at most once. Maps to the
/// <see cref="InboxCapabilities.IdempotentConsumer"/> capability.
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Atomically records a (consumer, message-key) as seen. Returns <see cref="InboxAdmission.Admitted"/>
    /// the first time and <see cref="InboxAdmission.Duplicate"/> for every subsequent redelivery.
    /// </summary>
    Task<InboxAdmission> TryAdmitAsync(string consumer, string messageKey, CancellationToken cancellationToken = default);

    /// <summary>Marks an admitted message as fully processed.</summary>
    Task MarkProcessedAsync(string consumer, string messageKey, CancellationToken cancellationToken = default);

    /// <summary>Whether the given (consumer, message-key) has been marked processed.</summary>
    Task<bool> IsProcessedAsync(string consumer, string messageKey, CancellationToken cancellationToken = default);
}
