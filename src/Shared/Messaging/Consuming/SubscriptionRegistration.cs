namespace Messaging;

// What to do with one subject: two lambdas created by SubscriptionBuilder.Handle<TEvent, THandler>(),
// where the types are still known. The processor calls them without knowing the types.
internal sealed record HandlerEntry(Func<string, object?> Deserialize, Func<IServiceProvider, object, MessageContext, CancellationToken, Task> Invoke);

// One topic + subscription and everything it can handle. Shared (not copied) between
// the builder, which fills it, and the SubscriptionProcessor, which reads it at startup.
internal sealed class SubscriptionRegistration
{
    public required string Topic { get; init; }
    public required string Subscription { get; init; }

    public Dictionary<string, HandlerEntry> Handlers { get; } = [];
}
