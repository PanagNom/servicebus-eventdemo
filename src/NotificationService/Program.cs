using Azure.Messaging.ServiceBus;
using NotificationService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => new ServiceBusClient(builder.Configuration["ServiceBus:ConnectionString"]));
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateProcessor(builder.Configuration["ServiceBus:TopicName"], builder.Configuration["ServiceBus:SubscriptionName"], new ServiceBusProcessorOptions
{
    AutoCompleteMessages = false,
    MaxConcurrentCalls = 1
}));
builder.Services.AddSingleton<IProcessedMessageStore, InMemoryProcessedMessageStore>();
builder.Services.AddHostedService<OrderPlacedConsumer>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
