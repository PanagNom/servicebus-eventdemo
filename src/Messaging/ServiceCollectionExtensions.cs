using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;

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
}
