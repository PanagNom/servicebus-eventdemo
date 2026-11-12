namespace NotificationService;

using System.Collections.Concurrent;

/// <summary>
/// Remembers which messages have already been handled, so a redelivered message
/// (at-least-once delivery) doesn't repeat its side effects.
/// </summary>
public interface IProcessedMessageStore
{
    bool HasBeenProcessed(string messageId);
    void MarkProcessed(string messageId);
}

/// <summary>
/// Demo-only store. It's lost on restart and not shared between instances.
/// In production, use a database table with a unique constraint on MessageId,
/// written in the same transaction as the work (the "inbox" pattern).
/// </summary>
public sealed class InMemoryProcessedMessageStore : IProcessedMessageStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processed = new();

    public bool HasBeenProcessed(string messageId) => _processed.ContainsKey(messageId);

    public void MarkProcessed(string messageId) => _processed.TryAdd(messageId, DateTimeOffset.UtcNow);
}
