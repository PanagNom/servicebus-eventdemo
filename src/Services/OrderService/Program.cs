using Contracts;
using Messaging;
using OrderService.Endpoints;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddOpenApi();

builder.Services.AddServiceBus(builder.Configuration["ServiceBus:ConnectionString"] ?? throw new InvalidOperationException("ServiceBus:ConnectionString is not configured"))
    .AddEventPublisher(Topics.Orders);

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapMessagingDebugEndpoints();
}

app.MapOrderEndpoints();

app.Run();
