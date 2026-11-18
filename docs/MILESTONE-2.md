# Milestone 2: shared `Messaging` library, `ServiceDefaults` and the new topology

**Where we are:** Milestone 1 is done (commit `5c2ea22`, "Containerise the two services"). Milestone 2's six steps are all done, and every checkpoint scenario passes. Only the commit is left. The overall plan is in [ROADMAP.md](ROADMAP.md). The [Progress](#progress) section at the end has the details and the results.

**Next session:** start roadmap step 3 (a database per service, the outbox and the inbox). First, confirm the one fan-out check listed under "Not yet confirmed".

---

## What changes, and what doesn't

From the outside, **nothing changes**. The same POST and the same `EMAIL SENT` happen. Inside, the plumbing moves out of the services into libraries:

```
TODAY                                   AFTER M2
OrderService/Program.cs                 OrderService/Program.cs
  builds ServiceBusMessage by hand        publisher.PublishAsync(evt, ...)   ← 1 line
  /debug/peek, /debug/peek-dlq            app.MapMessagingDebugEndpoints()
NotificationService/                    NotificationService/
  OrderPlacedConsumer (116 lines:         OrderPlacedHandler : IEventHandler<OrderPlacedEvent>
   validate, deserialize, dedupe,           only: poison/flaky hooks + "EMAIL SENT"
   DLQ, complete, shutdown)
                                        Messaging/  ← all the generic plumbing, written once
                                        ServiceDefaults/ ← /health (OpenTelemetry later)
```

**Why it matters:** in M4 you add 3 more services. Without this library, you'd copy `OrderPlacedConsumer`'s 116 lines into each of them, and fix every bug five times.

---

## The order to do it in (check each step before the next)

### Step 1: Contracts (pure C#, no infrastructure)

Add the 5 new event records next to [OrderPlacedEvent.cs](../src/Contracts/OrderPlacedEvent.cs), and extend `OrderPlacedEvent` with `ProductId`, `Quantity`, `ShippingAddress` and `PaymentMethod`. Downstream services will keep their own copies of these fields (see the roadmap).

| Event | Fields beyond `OrderId` |
|---|---|
| `StockReservedEvent` | ProductId, Quantity, ReservedAt |
| `StockReservationFailedEvent` | ProductId, Reason |
| `PaymentSucceededEvent` | PaymentId, Amount, Currency |
| `PaymentFailedEvent` | Reason |
| `OrderShippedEvent` | TrackingNumber, ShippedAt |

Also add two constants classes:
- **`EventSubjects`:** `"OrderPlaced"`, `"StockReserved"`, and so on.
- **`Topics`:** `"orders"`, `"inventory"`, `"payments"`, `"shipping"`.

Topic names and subjects are part of the contract between services. Subscription names aren't: they belong to each consumer.

**Checkpoint:** `dotnet build`. Keep the `Event` suffix the same across all 6 names.

---

### Step 2: the new topology in [Config.json](../infra/Config.json)

**Your old code keeps working against it**, so it's a safe step to check on its own. Replace the single `orders` topic with 4 topics:

| Topic | Subscription | Rule (correlation filter on `Label`) | MaxDeliveryCount |
|---|---|---|---|
| `orders` | `inventory`, `payment`, `shipping`, `notifications` | none (only one event type) | 3 |
| `inventory` | `payment` | `Label = StockReserved` | **10** |
| | `order` | none | 3 |
| | `notifications` | none | **10** |
| `payments` | `shipping` | `Label = PaymentSucceeded` | **10** |
| | `inventory` | `Label = PaymentFailed` | 3 |
| | `order` | none | 3 |
| | `notifications` | none | **10** |
| `shipping` | `order` | none | 3 |
| | `notifications` | none | **10** |

The **10s** are the subscriptions where the event can arrive *before* that service has processed its own copy of `OrderPlaced`. Its retries need to last longer. `audit` goes away; fan-out is now built into the design.

**Rule format.** This was checked against Microsoft's emulator sample Config.json. Rules sit on the subscription, next to `Properties`. Filtering on Subject uses the key **`Label`**, the AMQP name for Subject:

```json
{
  "Name": "payment",
  "Properties": { "...": "same properties as your existing subscriptions" },
  "Rules": [
    { "Name": "only-stock-reserved",
      "Properties": { "FilterType": "Correlation", "CorrelationFilter": { "Label": "StockReserved" } } }
  ]
}
```

On each topic, set `RequiresDuplicateDetection: true` with `DuplicateDetectionHistoryTimeWindow: "PT5M"`. This is free protection against double sends from the outbox in M3.

> **Emulator limit:** the emulator refuses to start with a window above **5 minutes** (*"Max Duplicate detection window supported 5m"*). Real Azure Service Bus allows up to 7 days.

**Checkpoint:**
- `docker compose up -d --force-recreate emulator`. Recreating empties the broker, because Docker only reads Config.json when it creates the container.
- Check `docker compose logs emulator` for errors.
- POST an order: the M1 behavior should be unchanged.

---

### Step 3: `ServiceDefaults` (small, and it shows the cross-project wiring)

- Create a class library. Add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` so it can use ASP.NET types.
- Add two extension methods:
  - `AddServiceDefaults(this IHostApplicationBuilder)`: registers health checks
  - `MapDefaultEndpoints(this WebApplication)`: maps `/health`
- Reference it from both services, call both methods, and add the project to `EventDemo.slnx`.

**⚠️ Your Dockerfiles break here.** Every project a service references must be copied **before** `dotnet restore`. That's lines 6–7 of each Dockerfile: add a `COPY` for each new csproj, using the same mirror-the-repo rule:

```
source:       src/ServiceDefaults/ServiceDefaults.csproj
destination:  ./src/ServiceDefaults/
```

This happens again with `Messaging`.

**Checkpoint:** `docker compose up -d --build`, then `curl.exe http://localhost:5085/health` and `curl.exe http://localhost:5289/health` → `Healthy`.

---

### Step 4: Messaging, part 1: publishing

- Create a class library with the `Azure.Messaging.ServiceBus` package and the same `FrameworkReference`.
- **`MessageEnvelope`:** move the message-building lines in OrderService's Program.cs (`new ServiceBusMessage(...)`, MessageId, Subject, ContentType, CorrelationId, SchemaVersion) here. It takes `(event, subject, messageId, correlationId)` and returns a `ServiceBusMessage`.
- **`IEventPublisher`:** `PublishAsync<TEvent>(TEvent evt, string subject, string messageId, string correlationId, CancellationToken ct)`. The implementation holds a `ServiceBusSender` for **the service's own topic**: one topic per producer.
- **A registration helper:** e.g. `services.AddServiceBus(connectionString)` (registers the client singleton) and `.AddEventPublisher(Topics.Orders)`.
- **CorrelationId changes now to `OrderId`.** Every event in a saga is about one order, so OrderId is the key that ties them together. Tracing gets its own id in M5. Note this change in README §4.

**Checkpoint:** OrderService publishes through `IEventPublisher`. Its Program.cs no longer mentions `ServiceBusMessage`, and the POST still returns 202.

---

### Step 5: Messaging, part 2: debug endpoints

`app.MapMessagingDebugEndpoints()` maps `GET /debug/peek/{topic}/{subscription}` and `/debug/dlq/{topic}/{subscription}`. The library doesn't know your event types, so return `Body` as a string.

- Map them only in Development, and set `ASPNETCORE_ENVIRONMENT: Development` on both services in compose. Now that the HTTPS redirect is gone, that's safe, and you get OpenAPI back.
- Delete the old `/debug/peek*` routes and update [OrderService.http](../src/OrderService/OrderService.http).

**Checkpoint:** POST one order, then peek `orders/inventory`, `orders/payment` and `orders/shipping`. **Each should hold its own copy**, waiting for services that don't exist yet. That's fan-out you can see.

---

### Step 6: Messaging, part 3: consuming (the big one)

- **`IEventHandler<TEvent>`:** `Task HandleAsync(TEvent evt, MessageContext ctx, CancellationToken ct)`. `MessageContext` carries MessageId, CorrelationId and DeliveryCount.
- **Move `IProcessedMessageStore` into Messaging.** The idempotency check becomes generic. M3 swaps it for the database inbox.
- **`SubscriptionProcessor`** (a `BackgroundService`, one instance per topic and subscription). Move [OrderPlacedConsumer.cs](../src/NotificationService/OrderPlacedConsumer.cs)'s flow into it:
  1. Look up the Subject among the registered handlers. If it's unknown, or the SchemaVersion isn't 1, dead-letter it.
  2. Deserialize to the registered type. Invalid JSON → dead-letter.
  3. If the message was already processed: log `Duplicate`, complete, return.
  4. Create a DI scope with `IServiceScopeFactory.CreateAsyncScope()` and resolve the handler from it. Handlers will need a scoped DbContext in M3.
  5. Call the handler, mark the message processed, complete.
  6. Keep the `StopProcessingAsync` shutdown logic and the error handler.
- **Registration:** e.g. `services.AddSubscription(Topics.Orders, "notifications").Handle<OrderPlacedEvent, OrderPlacedHandler>(EventSubjects.OrderPlaced)`.
- **NotificationService** gets `OrderPlacedHandler`. It keeps only the test hooks and the `EMAIL SENT` log; `OrderPlacedConsumer` is deleted.

**Pitfalls in this step:**
- **`AddHostedService<SubscriptionProcessor>()` registers only one instance.** Internally it uses `TryAddEnumerable`, which ignores a second registration of the same type. Notification will have 4 subscriptions in M4, so register each one with a factory: `services.AddSingleton<IHostedService>(sp => new SubscriptionProcessor(...))`.
- **Dispatching without reflection:** at `Handle<TEvent, THandler>()` time, you know both generic types. Store a lambda per subject that captures them (deserialize to `TEvent`, resolve `THandler`, call it). The processor then just looks up the subject and calls the lambda.
- **The `flaky` hook:** completing is now the library's job, so the handler can't "forget to complete" anymore. Give `MessageContext` a test-only switch, e.g. `ctx.SimulateLostComplete()`. The processor still marks the message processed but skips `CompleteMessageAsync`. Comment it clearly as a test hook.
- **Keep `AutoCompleteMessages = false` and `MaxConcurrentCalls = 1`.**

**Checkpoint (the milestone's "done when"):** rebuild, then run all M1 scenarios again:

| Scenario | Expect |
|---|---|
| normal order | `EMAIL SENT` |
| `poison` | 3 deliveries, then in `/debug/dlq/orders/notifications` |
| `flaky` | `Duplicate … skipping` after ~30 s |
| stop notification-service → POST → start | processed on startup |
| `/health` on 5085 and 5289 | `Healthy` |

---

## Don't forget

- **Each new project:** add it to `EventDemo.slnx` and add a `COPY` line to **both** Dockerfiles.
- **Config.json changes:** they need `docker compose up -d --force-recreate`.
- **Something to verify for yourself:** does a subscription with explicit rules also keep the `$Default` "accept everything" rule in the emulator? You'll only be able to test this in M4, when there's more than one subject on a topic. After a `card-declined` order, `payments/shipping` should stay empty.

## Be able to explain

- **Correlation filter vs SQL filter:** a correlation filter is an exact-match lookup, which the broker can index; a SQL filter is evaluated per message.
- **Why a permanent failure skips retries:** bad data fails the same way every time, so retrying only delays the dead-letter.
- **The cost of a shared library across microservices:** every service depends on its version, so keep it to plumbing, never business logic.

## Progress

- [x] **Step 1: Contracts.** 6 event records, `Topics`, and `EventSubjects` trimmed to the 6 subjects that exist. `PaymentProcessed` was renamed to `PaymentSucceeded` so it matches the `payments/shipping` filter.
- [x] **Step 2: Config.json topology.** 4 topics, 13 subscriptions, correlation rules on `Label`, duplicate detection (`PT5M`, the emulator's maximum; `PT10M` made it crash at startup).
- [x] **Step 3: ServiceDefaults and `/health`.** `AddServiceDefaults()` and `MapDefaultEndpoints()` in both services. Checked: `/health` → `Healthy` on 5085 and 5289.
- [x] **Step 4: Messaging, publishing.** `MessageEnvelope`, `IEventPublisher`, `ServiceBusEventPublisher`, and `AddServiceBus()` / `AddEventPublisher(topic)` (a factory, so the sender stays private). OrderService publishes through it with `OrderId` as both MessageId and CorrelationId. Checked: POST → 202 → `EMAIL SENT`, and the consumer log shows CorrelationId = OrderId.
- [x] **Step 5: Messaging, debug endpoints.** `MapMessagingDebugEndpoints()` in [DebugEndpoints.cs](../src/Messaging/Diagnostics/DebugEndpoints.cs): `GET /debug/peek/{topic}/{subscription}` and `GET /debug/peek-dlq/{topic}/{subscription}`. Bodies are raw strings, so the library stays free of Contracts. The DLQ view shows `DeadLetterReason`, `DeadLetterErrorDescription`, `DeliveryCount` and `EnqueuedTime`. Mapped in Development only, and compose sets `ASPNETCORE_ENVIRONMENT: Development`. OrderService no longer references `Azure.Messaging.ServiceBus` directly. [OrderService.http](../src/OrderService/OrderService.http) has one-click peeks for all four `orders/*` subscriptions, the DLQ and both `/health` endpoints. Checked: every GET in the `.http` file returns 200, and `orders/inventory`, `orders/payment` and `orders/shipping` exist, which confirms the emulator loaded the new topology.
- [x] **Step 6: Messaging, consuming.** `IProcessedMessageStore` moved into Messaging. New: `IEventHandler<TEvent>`; `MessageContext` with a test-only `SimulateLostComplete()`; `SubscriptionRegistration` and `SubscriptionBuilder` with `Handle<TEvent, THandler>(subject)`; and `SubscriptionProcessor`. The processor runs: subject lookup → SchemaVersion → deserialize → duplicate check keyed `subscription:MessageId` → handler in its own DI scope → mark processed → complete, with permanent failures dead-lettered. `AddSubscription` registers each processor with `AddSingleton<IHostedService>(factory)`. In NotificationService, `OrderPlacedHandler` replaces `OrderPlacedConsumer`, and its direct Service Bus reference and topic/subscription settings are gone.
- [x] **All checkpoint scenarios pass, README updated.** Run against the rebuilt containers:

  | Scenario | Result |
  |---|---|
  | `/health` on 5085 and 5289 | `Healthy (HTTP 200)` on both |
  | Normal order | 202 → `Received OrderPlaced … on orders/notifications` → `EMAIL SENT` |
  | poison | Deliveries 1, 2, 3 throw; DLQ shows `MaxDeliveryCountExceeded`, `DeliveryCount 3` (this also covers the Step 5 DLQ check) |
  | flaky | Delivery 1: `EMAIL SENT`, then TEST HOOK. Delivery 2 (~30 s later): `Duplicate … skipping` |
  | Consumer offline | Posted while stopped; after `start`: `Received`, then `EMAIL SENT` |

  The README now covers Messaging and ServiceDefaults, the four topics and filters, CorrelationId = OrderId, the new debug URLs, the end of the `audit` subscription, and new troubleshooting rows.
- [ ] Committed

**Milestone 2 is complete once it's committed.** Next is roadmap step 3 (a database per service, the outbox and the inbox); see [ROADMAP.md](ROADMAP.md).

### Not yet confirmed

- **Fan-out with the same MessageId in all four `orders/*` peeks.** In the consumer-offline run, a peek of `orders/notifications` 2 seconds after the POST came back empty, even though the message was processed after the restart. That matches the emulator delay in the README troubleshooting table ("`/debug/peek/...` is empty right after a POST"). To confirm: stop notification-service, POST, wait about 5 seconds, then run the four **Peek orders/…** requests in [OrderService.http](../src/OrderService/OrderService.http).

### Open nits

- [DebugEndpoints.cs](../src/Messaging/Diagnostics/DebugEndpoints.cs) line 12: trailing spaces (still there).
- [OrderService appsettings.json](../src/OrderService/appsettings.json): `ServiceBus:TopicName` is no longer read (the topic comes from `Topics.Orders`). It can go.
