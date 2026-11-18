namespace Messaging;

/// <summary>
/// Remembers which messages have already been handled, so a redelivered message
/// (at-least-once delivery) doesn't repeat its side effects.
/// </summary>
/// <remarks>
/// <see cref="SubscriptionProcessor"/> passes a key of the form <c>"{subscription}:{messageId}"</c>,
/// so the same message received on two subscriptions is handled once on each.
/// </remarks>
public interface IProcessedMessageStore
{
    bool HasBeenProcessed(string messageId);
    void MarkProcessed(string messageId);
}
