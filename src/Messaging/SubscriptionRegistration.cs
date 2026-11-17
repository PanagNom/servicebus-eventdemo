using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Messaging;

internal sealed record HandlerEntry(
    Func<string, object?> Deserialize,
    Func<IServiceProvider, object, MessageContext, CancellationToken, Task> Invoke);

// One topic + subscription and everything it can handle. Shared (not copied) between
// the builder, which fills it, and the SubscriptionProcessor, which reads it at startup.
internal sealed class SubscriptionRegistration
{
    public required string Topic { get; init; }
    public required string Subscription { get; init; }

    public Dictionary<string, HandlerEntry> Handlers { get; } = [];
}

// What AddSubscription(...) returns, so you can chain .Handle<…>() calls.
public sealed class SubscriptionBuilder
{
    private readonly IServiceCollection _services;
    private readonly SubscriptionRegistration _registration;

    internal SubscriptionBuilder(IServiceCollection services, SubscriptionRegistration registration)
    {
        _services = services;
        _registration = registration;
    }

    public SubscriptionBuilder Handle<TEvent, THandler>(string subject)
        where TEvent : class
        where THandler : class, IEventHandler<TEvent>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        // Two handlers for one subject would silently replace each other, so fail at startup instead.
        if (_registration.Handlers.ContainsKey(subject))
        {
            throw new InvalidOperationException(
                $"Subject '{subject}' already has a handler on {_registration.Topic}/{_registration.Subscription}.");
        }

        _services.AddScoped<THandler>();

        _registration.Handlers[subject] = new HandlerEntry(
            body => JsonSerializer.Deserialize<TEvent>(body),
            (sp, evt, ctx, ct) => sp.GetRequiredService<THandler>().HandleAsync((TEvent)evt, ctx, ct));

        return this;
    }
}
