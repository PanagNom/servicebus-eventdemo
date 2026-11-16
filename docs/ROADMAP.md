# Roadmap: EventDemo → 5 containerized microservices on Service Bus

**A self-build guide.** Milestones, design, API pointers, pitfalls and a "done when" check for each step. The code is written by hand, milestone by milestone.

## Context

Today the repo has OrderService (publisher) and NotificationService (consumer), run with `dotnet run` against the Service Bus emulator and mssql in Rancher Desktop (dockerd). The goal:

- 5 services, each in its own container, talking only through Service Bus
- a database per service, with the outbox and inbox patterns actually built (README §6e currently calls them "discussion, not built")
- distributed tracing
- Kubernetes later

**Decisions so far:** order-fulfillment saga (Order, Inventory, Payment, Shipping, Notification) · choreography · a SQL database per service with EF Core · the raw `Azure.Messaging.ServiceBus` SDK behind your own small shared library · one topic per producing service · OpenTelemetry with the Aspire Dashboard · no gateway · Kubernetes as the last, future milestone.

## Target design (refer back to this)

```
Client ─▶ OrderService ─(outbox)─▶ topic orders: OrderPlaced
                                     subs: inventory, payment, shipping, notifications
          topic inventory: StockReserved | StockReservationFailed
                                     subs: payment (Label=StockReserved), order, notifications
          topic payments:  PaymentSucceeded | PaymentFailed
                                     subs: shipping (Label=PaymentSucceeded), inventory (Label=PaymentFailed), order, notifications
          topic shipping:  OrderShipped
                                     subs: order, notifications
```

- **Order status:** Pending → StockReserved → Paid → Shipped, or **Cancelled(reason)**. Guard the transitions so a late event can't move an order backwards.
- **Compensation:** on PaymentFailed, Inventory releases the reservation and Order marks the order Cancelled.
- **Thin events plus local copies:** downstream events carry `OrderId` and their own facts. Services that need order data (Payment: amount and method; Shipping: address; Notification: email) subscribe to `OrderPlaced` and store what they need.
- **Envelope:** CorrelationId = OrderId through the whole chain. Derived events get deterministic MessageIds (`{OrderId}:StockReserved`). Turn on duplicate detection on the topics.
- **Test hooks:** `SKU-LOW` (stock 1) or an unknown SKU causes out of stock; `paymentMethod: "card-declined"` causes a declined payment; the existing `poison` and `flaky` hooks stay.

---

## Milestone 1: Containerize the 2 existing services

**Goal:** `docker compose up -d --build` from `infra/` runs mssql, the emulator, OrderService and NotificationService, and today's scenarios still work.

**Tasks**
- Add a `Dockerfile` per service: multi-stage, with the **repo root as build context** (the services reference `../Contracts`).
  - Stage 1: `mcr.microsoft.com/dotnet/sdk:10.0`. Copy `global.json`, `nuget.config` and **only the `.csproj` files** first, then `dotnet restore`, then copy `src/` and `dotnet publish -c Release -o /app`.
  - Stage 2: `mcr.microsoft.com/dotnet/aspnet:10.0`, `USER $APP_UID`, `ENTRYPOINT ["dotnet", "OrderService.dll"]`. The image listens on 8080 by default (`ASPNETCORE_HTTP_PORTS`).
- Add a root `.dockerignore`: `**/bin`, `**/obj`, `infra/.env`, `.git`, `.vs`.
- Add both services to [infra/docker-compose.yaml](../infra/docker-compose.yaml):
  - `build: { context: .., dockerfile: src/OrderService/Dockerfile }`
  - `ports: "5085:8080"`
  - join the `sb-emulator` network
  - `depends_on: emulator`
- Pass config as environment variables. `__` maps to `:`, so `ServiceBus__ConnectionString` becomes `ServiceBus:ConnectionString`.

**Pitfalls**
- Inside the compose network the emulator **isn't `localhost`**. Use `Endpoint=sb://servicebus-emulator;...` (its service or container name).
- `UseHttpsRedirection()` in [OrderService/Program.cs](../src/OrderService/Program.cs#L22) causes trouble on HTTP-only containers. Remove it or limit it to Development.
- `global.json` must allow the SDK version that's in the image. Check `rollForward`.
- User secrets aren't loaded in a Release container (they only load in Development), which is why config comes from environment variables.
- The emulator restarts while SQL is still starting. Your services must tolerate a broker that isn't ready yet (the processor retries; the first `SendMessageAsync` may fail).

**Done when:** `docker compose ps` shows 4 containers running, POST `/orders` against `localhost:5085` logs `EMAIL SENT` in `docker compose logs -f notification-service`, and poison → DLQ still works.

**Be able to explain:** why the csproj files are copied before the source (layer caching); why the container runs as non-root; image vs container.

---

## Milestone 2: Shared `Messaging` library, `ServiceDefaults` and the new topology

**Goal:** the consume and publish plumbing lives in one library, and the services only contain business handlers.

**Tasks**
- **`src/Contracts`:** add the 6 events (extend `OrderPlacedEvent` with ProductId, Quantity, ShippingAddress, PaymentMethod) and an `EventSubjects` constants class.
- **`src/Messaging`** (class library; `<FrameworkReference Include="Microsoft.AspNetCore.App" />` lets it use hosting and DI):
  - **An envelope builder:** move lines 37–42 of OrderService/Program.cs into it.
  - **`IEventHandler<TEvent>`:** `Task HandleAsync(TEvent evt, MessageContext ctx, CancellationToken ct)`. `MessageContext` exposes MessageId, CorrelationId and DeliveryCount.
  - **`SubscriptionProcessor`:** a `BackgroundService`, one per (topic, subscription). Move the logic from [OrderPlacedConsumer.cs](../src/NotificationService/OrderPlacedConsumer.cs) here: validate Subject and SchemaVersion, deserialize, dead-letter permanent failures, dispatch by Subject to the registered handler, complete.
  - **A registration API:** e.g. `services.AddSubscription("inventory", "payment").Handle<StockReserved, StockReservedHandler>(EventSubjects.StockReserved)`.
- **Handler scope:** create one with `IServiceScopeFactory.CreateAsyncScope()` per message. Handlers will need scoped DbContexts in M3.
- **`src/ServiceDefaults`:** `AddServiceDefaults(this IHostApplicationBuilder)`. For now just health checks (`/health`); OpenTelemetry comes in M5.
- **[infra/Config.json](../infra/Config.json):** 4 topics and their subscriptions with **rules** (correlation filter on `Label`, which is Subject). Look up the emulator's Config.json rule schema in Microsoft's emulator repo samples. Several rules on one subscription combine as OR. Set `MaxDeliveryCount: 10` on subscriptions where an out-of-order prerequisite is possible.

**Pitfalls**
- A subscription with no rules receives **everything** (`$Default` TrueFilter). If you add filters, make sure the default rule isn't still there.
- `ServiceBusProcessor` uses `MaxConcurrentCalls`; keep it at 1 while learning so the logs stay readable.
- Keep `AutoCompleteMessages = false`.
- Don't let the library know about specific events. It dispatches on Subject → registered type.

**Done when:** Order and Notification run on the library with no change in behavior, and `/debug/peek/{topic}/{sub}` and `/debug/dlq/{topic}/{sub}` work for any subscription.

**Be able to explain:** correlation filter vs SQL filter; why permanent failures skip retries; the trade-offs of a shared library across microservices (coupling, versioning).

---

## Milestone 3: A database per service, outbox and inbox

**Goal:** the README's §6c limits ("lost on restart, not shared") and §6e (dual write) are solved in code.

**Tasks**
- **Packages:** `Microsoft.EntityFrameworkCore.SqlServer`, plus `dotnet-ef` as a local tool (`dotnet new tool-manifest`, `dotnet tool install dotnet-ef`).
- **One database per service** on the existing `mssql` container: `Server=mssql;Database=OrderDb;User Id=sa;Password=...;TrustServerCertificate=true`.
- **In Messaging:**
  - `OutboxMessage` (Id, Topic, Subject, MessageId, CorrelationId, Body, TraceParent, CreatedAt, SentAt?)
  - `InboxMessage` with composite PK (MessageId, Subscription)
  - an `IMessagingDbContext` interface
  - a `modelBuilder` extension that maps both tables
- **`IOutbox.Enqueue(...)`:** only **adds** a row to the DbContext. It never sends.
- **`OutboxRelay<TDbContext>`** (BackgroundService):
  - poll unsent rows (ordered by CreatedAt), send, set `SentAt`, save
  - use `PeriodicTimer` with roughly 1 s polling
- **Change the consume flow in `SubscriptionProcessor`:**
  1. check the inbox, and if the message is a duplicate, complete it
  2. run the handler
  3. add the inbox row
  4. one `SaveChangesAsync()` covering the domain change, inbox row and outbox rows, all in one transaction
  5. complete
- **OrderService:**
  - `POST /orders` saves the `Order` (Pending) and enqueues `OrderPlaced` in the **same** `SaveChanges`, then returns 202
  - add `GET /orders/{id}`
- Apply migrations at startup with `db.Database.MigrateAsync()` and a retry loop, because SQL starts slowly. Use `EnableRetryOnFailure()` on the provider.

**Pitfalls**
- **NotificationService:** sending an email is a side effect *outside* the database, so it can't be in the transaction. Keep the check → work → record order. Emails are still at-least-once, and you should be able to say why.
- **Unique-key violation on the inbox insert:** a concurrent duplicate. Catch `DbUpdateException`, then complete the message.
- **Outbox relay crash after send but before `SentAt`:** this causes a resend, which duplicate detection and inbox absorb. That's why MessageIds must be deterministic.
- **Scaling the relay:** two relays (two replicas) would double-send. That's fine for now; see M7.

**Done when:**
- Restarting notification-service within 30 s of a `flaky` order still logs `Duplicate … skipping`.
- Stopping the emulator, posting an order (it's accepted, the row is in the outbox) and starting the emulator again gets the event delivered.

**Be able to explain:** the dual-write problem; outbox vs CDC; why the inbox and the domain write must share a transaction.

---

## Milestone 4: Inventory, Payment, Shipping and the saga

**Goal:** the full choreographed flow, with compensation.

**Tasks**
- 3 new web projects. Copy the structure of the services you already have, add them to `EventDemo.slnx` and add a Dockerfile and compose entry for each (ports 5086, 5087, 5088).
- **Inventory:**
  - a seeded `Products` table and a `Reservations` table
  - OrderPlaced → reserve stock → StockReserved or StockReservationFailed
  - PaymentFailed → release the reservation
  - `GET /products`
- **Payment:**
  - OrderPlaced → store amount, currency and method
  - StockReserved → charge (simulated) → PaymentSucceeded, or PaymentFailed for `card-declined`
- **Shipping:**
  - OrderPlaced → store the address
  - PaymentSucceeded → create a shipment with a tracking number → OrderShipped
- **Order:** consume the 5 downstream events and apply guarded status transitions.
- **Notification:** subscribe to all 4 topics, store email from OrderPlaced, and log one email per event type.

**Pitfalls**
- **No ordering across topics:** Payment can get StockReserved *before* its copy of OrderPlaced. Throw a transient exception (abandon → retry). That's why those subscriptions have a higher MaxDeliveryCount.
- **Reserving stock:** concurrent orders for the same SKU need optimistic concurrency (a `[Timestamp]` rowversion) or a conditional update (`UPDATE ... WHERE Stock >= @qty`).
- **Compensation must be idempotent too:** releasing the same reservation twice must not add the stock back twice.

**Done when:** happy path → Shipped; `SKU-LOW` × 5 → Cancelled(OutOfStock); `card-declined` → Cancelled(PaymentDeclined) with stock restored.

**Be able to explain:** choreography vs orchestration (where would you see "the flow" in each?); compensation vs rollback; eventual consistency as the client sees it (polling `GET /orders/{id}`).

---

## Milestone 5: Observability

**Goal:** one trace per order across all 5 services in the Aspire Dashboard.

**Tasks**
- Add the `mcr.microsoft.com/dotnet/aspire-dashboard` container (UI on `18888`, OTLP on `18889`, `DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true`). Pin a specific tag.
- In `ServiceDefaults`:
  - `OpenTelemetry.Extensions.Hosting` and the OTLP exporter
  - instrumentation for AspNetCore, Http and SqlClient
  - `.AddSource("Azure.Messaging.ServiceBus.*")`
  - logs sent over OTLP too
- Turn on Azure SDK tracing: `AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true)`.
- Set `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_SERVICE_NAME` per service in compose.

**Pitfall:** the outbox breaks the trace. The relay runs on its own timer, so `Activity.Current` there isn't the request's. Store `Activity.Current?.Id` in the outbox row and set it as the message's `Diagnostic-Id` when you send. Check that the SDK doesn't overwrite a preset value.

**Done when:** one POST shows a single trace: HTTP → outbox → Inventory → Payment → Shipping → Order and Notification.

---

## Milestone 6: README

Update as you go, then do a final pass:
- new architecture diagram and saga flow
- §6c/§6e moved to "built"
- the ordering lesson
- running with Rancher
- scenario table
- `.http` requests for each scenario

## Milestone 7 (future): Kubernetes on Rancher's k3s

- Deployment and Service per service; config in ConfigMaps, passwords and connection strings in Secrets.
- Readiness and liveness probes on `/health`.
- Migrations as a Job or init container, not on app startup.
- mssql and the emulator as StatefulSets, or still in compose and reached from the cluster.
- The outbox relay must be safe with several replicas: claim rows with `UPDLOCK, READPAST`, or use leader election.
- Ingress through Traefik, which Rancher already runs. Local images need to be visible to k3s; with the dockerd engine, images you build are already available.

## How to check each milestone

Every milestone ends with: `dotnet build EventDemo.slnx`, `docker compose up -d --build`, and that milestone's "Done when" scenarios run through [OrderService.http](../src/OrderService/OrderService.http). Commit once they pass.
