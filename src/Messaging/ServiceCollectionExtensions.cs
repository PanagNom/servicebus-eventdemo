using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Messaging;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddServiceBus(this IServiceCollection services, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }
        services.AddSingleton(_ => new ServiceBusClient(connectionString));

        return services;
    }

    public static IServiceCollection AddEventPublisher(this IServiceCollection services, string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new ArgumentException("Topic name cannot be null or empty.", nameof(topic));
        }

        services.AddSingleton<IEventPublisher>(sp =>
        {
            var client = sp.GetRequiredService<ServiceBusClient>();
            return new ServiceBusEventPublisher(client.CreateSender(topic));
        });

        return services;
    }

    public static SubscriptionBuilder AddSubscription(this IServiceCollection services, string topic, string subscription)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new ArgumentException("Topic name cannot be null or empty.", nameof(topic));
        }

        if (string.IsNullOrWhiteSpace(subscription))
        {
            throw new ArgumentException("Subscription name cannot be null or empty.", nameof(subscription));
        }

        var registration = new SubscriptionRegistration
        {
            Topic = topic,
            Subscription = subscription
        };

        services.TryAddSingleton<IProcessedMessageStore, InMemoryProcessedMessageStore>();

        // A factory registration, not AddHostedService<SubscriptionProcessor>(): AddHostedService
        // ignores a second registration of the same type, so only the first subscription would run.
        // The lambda captures this call's own `registration`.
        services.AddSingleton<IHostedService>(sp => new SubscriptionProcessor(
            registration,
            sp.GetRequiredService<ServiceBusClient>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IProcessedMessageStore>(),
            sp.GetRequiredService<ILogger<SubscriptionProcessor>>()));

        return new SubscriptionBuilder(services, registration);
    }
}
