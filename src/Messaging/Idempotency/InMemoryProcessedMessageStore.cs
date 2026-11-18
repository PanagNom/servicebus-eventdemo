using System.Collections.Concurrent;

namespace Messaging;

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
