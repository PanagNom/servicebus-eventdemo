using Contracts;
using Messaging;
using NotificationService.Handlers;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddServiceBus(builder.Configuration["ServiceBus:ConnectionString"] ?? throw new InvalidOperationException("ServiceBus:ConnectionString is not configured"))
    .AddSubscription(Topics.Orders, "notifications").Handle<OrderPlacedEvent, OrderPlacedHandler>(EventSubjects.OrderPlaced);

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", () => "Hello World!");

app.Run();
