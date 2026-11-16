# Milestone 2: shared `Messaging` library, `ServiceDefaults` and the new topology

**Where we are:** Milestone 1 is done (commit `5c2ea22`, "Containerise the two services"). Milestone 2 is in progress: Steps 1–4 are done and committed. The overall plan is in [ROADMAP.md](ROADMAP.md). The [Progress](#progress) section at the end has the details and what to do first.

**Next session:** recreate the emulator (see "Before Step 5" under Progress), then do Step 5.

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

On each topic, set `RequiresDuplicateDetection: true` with `DuplicateDetectionHistoryTimeWindow: "PT10M"`. This is free protection against double sends from the outbox in M3.

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
- [x] **Step 2: Config.json topology.** 4 topics, 13 subscriptions, correlation rules on `Label`, duplicate detection (`PT10M`). Reviewed, but **its checkpoint hasn't run yet**: the running emulator still has the old topology (see below).
- [x] **Step 3: ServiceDefaults and `/health`.** `AddServiceDefaults()` and `MapDefaultEndpoints()` in both services. Checked: `/health` → `Healthy` on 5085 and 5289.
- [x] **Step 4: Messaging, publishing.** `MessageEnvelope`, `IEventPublisher`, `ServiceBusEventPublisher`, and `AddServiceBus()` / `AddEventPublisher(topic)` (a factory, so the sender stays private). OrderService publishes through it with `OrderId` as both MessageId and CorrelationId. Checked: POST → 202 → `EMAIL SENT`, and the consumer log shows CorrelationId = OrderId.
- [ ] Step 5: Messaging, debug endpoints
- [ ] Step 6: Messaging, consuming
- [ ] All checkpoint scenarios pass, README updated, committed

### Before Step 5 (do this first)

1. **Recreate the emulator** so it loads the new Config.json. It has been running since before Step 2, so it still has the old `orders` → `notifications` + `audit` topology. Step 5's checkpoint (peeking `orders/inventory`, `orders/payment`, `orders/shipping`) needs the new subscriptions. Recreating empties the broker.
   ```powershell
   cd infra
   docker compose up -d --force-recreate emulator
   docker compose logs -f emulator      # wait until it's ready; look for errors about entities or rules
   ```
2. **Add `SQL_WAIT_INTERVAL=15` to `infra/.env`.** It's listed in README §8 and silences the compose warning.
3. POST one order to confirm that M1 behavior still works on the new topology: 202, then `EMAIL SENT`.

### Open nits (small, fix when convenient)

- [ServiceBusEventPublisher.cs](../src/Messaging/ServiceBusEventPublisher.cs): the closing `}` of `PublishAsync` sits on the `await` line. Also add a blank line after the field.
- [OrderService Program.cs](../src/OrderService/Program.cs): split the long `AddServiceBus(...)` line over 2–3 lines.
- [MessageEnvelope.cs](../src/Messaging/MessageEnvelope.cs), optional: `ArgumentNullException.ThrowIfNull(payload);`.

### Step 5 in short (details above)

- Add `MapMessagingDebugEndpoints()` to Messaging: `GET /debug/peek/{topic}/{subscription}` and `GET /debug/dlq/{topic}/{subscription}`, returning `Body` as a string.
- Map them only in Development, and set `ASPNETCORE_ENVIRONMENT: Development` on both services in `docker-compose.yaml`.
- Delete the old `/debug/peek` and `/debug/peek-dlq` from OrderService, then remove its own `Azure.Messaging.ServiceBus` package reference and `using`: they come through Messaging now.
- Update [OrderService.http](../src/OrderService/OrderService.http) to the new URLs.
