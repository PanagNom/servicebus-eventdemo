# Milestone 3: a database per service, the outbox and the inbox

**Where we are:** Milestone 2 is done, and the repo has been reorganised (commit `0ab98ee`). Milestone 3 hasn't started. The overall plan is in [ROADMAP.md](../ROADMAP.md). Where this guide and the roadmap's M3 section differ, this guide wins.

**Decisions made while planning:**
- The outbox and inbox code goes in a **new `Messaging.EntityFrameworkCore` project**. `Messaging` stays broker-only, with no database dependency.
- **`IEventPublisher` is retired.** Services publish only through `IOutbox`, so a dual write can't creep back in.
- **Break it first.** Step 2 adds the Order database but still publishes directly, so you see the dual-write bug on your own machine before you fix it.

---

## What changes, and what doesn't

From the outside, almost nothing: the same POST, the same 202, the same `EMAIL SENT`, plus a new `GET /orders/{id}`. Inside, the two "demo only" weaknesses from README §6c and §6e get fixed:

```
TODAY (M2)                                     AFTER M3
OrderService                                   OrderService
  POST /orders                                   POST /orders
    publish OrderPlaced ── broker down? 500,       save Order + OutboxMessage   ← ONE SaveChanges, one transaction
                           and nothing saved       202
                                                 OutboxRelay (every ~1 s)
                                                   unsent row → send → SentAt = now
                                                 GET /orders/{id} → Pending

NotificationService                            NotificationService
  in-memory "processed" set                      Inbox table in NotificationDb
    lost on restart, not shared                    survives restarts, shared by every instance
```

**The layout when you're done** (new parts only):

```
src/Shared/Messaging.EntityFrameworkCore/      NEW: references Messaging + EF Core (Relational)
├─ ServiceCollectionExtensions.cs              AddOutbox<TDbContext>(topic), AddInbox<TDbContext>()
├─ ModelBuilderExtensions.cs                   modelBuilder.MapOutbox(), modelBuilder.MapInbox()
├─ Outbox/   OutboxMessage.cs, IOutbox.cs, EfOutbox.cs, OutboxRelay.cs
└─ Inbox/    InboxMessage.cs, EfProcessedMessageStore.cs

src/Services/OrderService/
├─ Domain/      Order.cs, OrderStatus.cs
├─ Data/        OrderDbContext.cs, Migrations/
└─ Endpoints/   OrderEndpoints.cs (POST + GET), PlaceOrderRequest.cs, OrderResponse.cs

src/Services/NotificationService/
└─ Data/        NotificationDbContext.cs, Migrations/
```

---

## The order to do it in (check each step before the next)

### Step 1: make SQL reachable, add the EF tooling (no C# yet)

**Compose** ([docker-compose.yaml](../../infra/docker-compose.yaml)):
- `mssql`: add `ports: ["1433:1433"]`. With it, `dotnet run` and `dotnet ef` on your machine can reach SQL.
- Both services: add `mssql` to `depends_on`, and add one connection string each:
  ```yaml
  ConnectionStrings__OrderDb: "Server=mssql;Database=OrderDb;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true"
  ```
  NotificationService gets `ConnectionStrings__NotificationDb` with `Database=NotificationDb`. Compose fills in `${MSSQL_SA_PASSWORD}` from `infra/.env`, so the password stays out of git.

**User secrets**, for `dotnet run` (README §8, option B):
```
dotnet user-secrets set "ConnectionStrings:OrderDb" "Server=localhost,1433;Database=OrderDb;User Id=sa;Password=<yours>;TrustServerCertificate=true" --project src/Services/OrderService
```
Do the same for NotificationService with `NotificationDb`.

**Packages:** add these to [Directory.Packages.props](../../Directory.Packages.props). All EF packages and the `dotnet-ef` tool must share **one** 10.0.x version.
- `Microsoft.EntityFrameworkCore.SqlServer` (services)
- `Microsoft.EntityFrameworkCore.Relational` (the new library: provider-neutral)
- `Microsoft.EntityFrameworkCore.Design` (services, for migrations). In the csproj: `<PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />`

**Tool:** run these from the repo root:
```
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.x
```
They create `.config/dotnet-tools.json`. Commit it, so anyone can run `dotnet tool restore` and get the same version.

**Pitfalls:**
- **Locally it's `localhost,1433`, with a comma.** SQL Server uses a comma before the port, not a colon. Inside compose it's just `mssql`.
- **Without `TrustServerCertificate=true` the connection fails.** SqlClient encrypts by default, and the container's certificate is self-signed.
- **Special characters in the password:** a `;` breaks the connection string, and a `$` gets interpolated by compose. Pick a password without them.
- **The emulator and your databases share one SQL Server.** The emulator creates its own databases, and yours sit next to them. That's fine: each *service* still owns its *database*.

**Checkpoint:**
- `docker compose up -d`
- `Test-NetConnection localhost -Port 1433` → `TcpTestSucceeded : True`
- `dotnet ef --version` → 10.0.x

---

### Step 2: the Order database, still publishing directly (break it)

**Domain** (`Domain/`):
- **`Order`:** Id (the OrderId string), CustomerId, Email, ProductId, Quantity, ShippingAddress, PaymentMethod, TotalAmount, Currency, Status, PlacedAt.
- **`OrderStatus`:** an enum with only `Pending` for now. M4 adds the rest.

**Data** (`Data/OrderDbContext.cs`):
- `DbSet<Order> Orders`
- In `OnModelCreating`: set the key, max lengths, `TotalAmount` with `HasPrecision(18, 2)`, and `Status` with `HasConversion<string>()`, so SQL shows `Pending` instead of `0`.

**Program.cs:**
```csharp
builder.Services.AddDbContext<OrderDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("OrderDb"), sql => sql.EnableRetryOnFailure()));
```

**First migration:**
```
dotnet ef migrations add InitialCreate --project src/Services/OrderService --output-dir Data/Migrations
```

**Apply it at startup:** after `builder.Build()` and before `app.Run()`, open a scope, resolve the DbContext and call `db.Database.MigrateAsync()`. Wrap it in a small retry loop (for example 10 tries, 3 s apart), because SQL starts more slowly than your service. `MigrateAsync` also **creates the database** if it doesn't exist yet.

**Endpoints:**
- **`POST /orders`:** build the `Order` (Pending), `db.Orders.Add(order)`, `await db.SaveChangesAsync()`, then `PublishAsync` as today, then 202. Do it in that order: save first, then publish.
- **`GET /orders/{id}`:** 200 with an `OrderResponse` record (Id, Status, PlacedAt…), or 404. Return a response record, not the entity. The entity is your storage shape, and the response is your API contract (the same reasoning as `PlaceOrderRequest`).

**Pitfalls:**
- **`dotnet ef` runs your Program.cs up to `builder.Build()`.** If `ServiceBus:ConnectionString` is missing from user secrets, your own `throw` stops the migration. Set the secrets first.
- **`decimal` without precision:** EF warns, and SQL silently uses `decimal(18,2)`. Say it explicitly.
- **Don't put `MigrateAsync` before `Build()`.** There's no service provider yet.

**Checkpoint, part 1 (it works):** POST → 202, `GET /orders/{id}` → `Pending`, and `EMAIL SENT` in the notification log. Look at the table:
```
docker exec -it mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<password>" -C -d OrderDb -Q "SELECT Id, Status, PlacedAt FROM Orders"
```
(The VS Code "SQL Server (mssql)" extension pointed at `localhost,1433` works too.)

**Checkpoint, part 2 (break it):**
1. `docker compose stop emulator`, then POST an order. The SDK retries for a while, then the POST fails with a 500.
2. Run the `SELECT` again. **The order is there, and its event will never be sent.** OrderService says "Pending", and every other service has never heard of it.

**Write down what you saw.** That's the dual-write problem from README §6e, on your own machine.

3. `docker compose start emulator` before you go on.

---

### Step 3: `Messaging.EntityFrameworkCore`, the outbox table and `IOutbox`

**New class library** `src/Shared/Messaging.EntityFrameworkCore`:
- references `Messaging` and `Microsoft.EntityFrameworkCore.Relational`
- added to `EventDemo.slnx`, and as a `COPY` line in **both** Dockerfiles

**`OutboxMessage`:**

| Column | Type | Notes |
|---|---|---|
| `Id` | `long` identity | PK |
| `MessageId` | `string` | **unique index**: one row per event |
| `Subject` | `string` | |
| `CorrelationId` | `string` | |
| `Body` | `string` | the serialized event JSON, `nvarchar(max)` |
| `CreatedAt` | `DateTimeOffset` | the relay sends in this order |
| `SentAt` | `DateTimeOffset?` | `null` = not sent yet |

There's no `Topic` column: one topic per producer, so the relay already knows it. `TraceParent` comes in M5 as a **second migration**, which is good practice for changing a schema.

**`modelBuilder.MapOutbox()`:** an extension on `ModelBuilder`. It maps the table `OutboxMessages`, the unique index on `MessageId` and an index on `(SentAt, CreatedAt)`.

**`IOutbox`**, and **`EfOutbox<TDbContext>`** registered as scoped:
```csharp
void Enqueue<TEvent>(TEvent @event, string subject, string messageId, string correlationId) where TEvent : class;
```
It serializes the event and calls `db.Set<OutboxMessage>().Add(...)`. **It never calls `SaveChanges` and never sends.** The caller's `SaveChanges` commits it together with everything else.

There's no `IMessagingDbContext` interface: `db.Set<OutboxMessage>()` works on any DbContext whose model calls `MapOutbox()`.

**`MessageEnvelope`:** add an overload that takes the JSON body as a string. The typed version serializes, then calls it. The relay uses the string version, so the envelope properties (Subject, ContentType, SchemaVersion…) are still set in one place.

**OrderService:** call `modelBuilder.MapOutbox()` in `OnModelCreating`, then run `dotnet ef migrations add AddOutbox …`.

**Checkpoint:** `dotnet build`; restart order-service; `SELECT * FROM OutboxMessages` returns an empty table. Nothing uses it yet.

---

### Step 4: the relay, and switching OrderService to the outbox

**`OutboxRelay<TDbContext>`** (a `BackgroundService`):
- **Each tick** of a `PeriodicTimer` (1 s):
  1. create a scope and resolve `TDbContext`
  2. read `Where(m => m.SentAt == null).OrderBy(m => m.CreatedAt).Take(50)`
  3. for each row: `MessageEnvelope` → `sender.SendMessageAsync` → `SentAt = now` → `SaveChangesAsync`
- **On a send failure:** log it, **stop this batch** and try again on the next tick. If you skip ahead, you send events out of order.
- **The sender:** create one `ServiceBusSender` for the service's topic, from the `ServiceBusClient` singleton, when the relay starts. Dispose it in `StopAsync`.

**Registration**, `services.AddOutbox<OrderDbContext>(Topics.Orders)`:
- `IOutbox` → `EfOutbox<TDbContext>`, scoped
- the relay as a hosted service (one per service, so `AddHostedService` is fine here)

**OrderService:** in `POST /orders`, replace `PublishAsync` with `outbox.Enqueue(...)` **before** the one `SaveChangesAsync`. The order and its event are now saved together, or not at all.

**Retire the direct publisher:** delete `IEventPublisher`, `ServiceBusEventPublisher` and `AddEventPublisher`. If the build still compiles, nothing used them. Keep `MessageEnvelope`, which the relay needs.

**Pitfalls:**
- **A crash after the send but before `SentAt` is saved resends the row.** The `MessageId` is the same, so the broker's duplicate detection (5 min) absorbs it, and the inbox (Step 5) catches anything later. This is why MessageIds must be deterministic.
- **Don't keep a DbContext in a field of the relay.** The relay is a singleton and a DbContext is scoped. Use a new scope each tick: the Step 5 lesson from MESSAGING-EXPLAINED §11.
- **Don't log every empty tick.** One line per second buries everything else.
- **`EnableRetryOnFailure` and transactions:** one `SaveChangesAsync` is already a transaction, so you're fine. If you ever call `BeginTransaction` yourself, you must wrap it in `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`, or EF throws.
- **Two OrderService replicas would both send the same rows.** That's harmless thanks to duplicate detection, but wasteful. It gets fixed in M7 (claim rows with `UPDLOCK, READPAST`).

**Checkpoint (the payoff):**
1. Normal POST → 202, then `EMAIL SENT` about a second later. The row now has `SentAt` filled.
2. **Run Step 2's break-it test again:** `docker compose stop emulator`, then POST → **202 straight away**. `SELECT MessageId, SentAt FROM OutboxMessages` shows `SentAt` as `NULL`.
3. `docker compose start emulator`. Once it's up, the relay sends the row and `EMAIL SENT` appears. **The order and its event can no longer be separated.**

---

### Step 5: the inbox in NotificationService

**`InboxMessage`:** `MessageId` + `Subscription` as a **composite PK**, plus `ProcessedAt`. Map it with `modelBuilder.MapInbox()`.

**Change `IProcessedMessageStore`** (in Messaging) to async, with two parameters:
```csharp
Task<bool> HasBeenProcessedAsync(string subscription, string messageId, CancellationToken ct);
Task MarkProcessedAsync(string subscription, string messageId, CancellationToken ct);
```
Update `InMemoryProcessedMessageStore` to match. It now builds the `"{subscription}:{messageId}"` key itself.

**`EfProcessedMessageStore<TDbContext>`** (scoped):
- **`HasBeenProcessedAsync`:** `AnyAsync` on the inbox.
- **`MarkProcessedAsync`:** adds the `InboxMessage` **and calls `SaveChangesAsync`**. That one save commits the handler's changes, any outbox rows and the inbox row together. **The rule that follows: handlers never call `SaveChanges` themselves.** Write it in the doc comment.

**Change `SubscriptionProcessor`:** the store can now be scoped, so the processor resolves it **from the message's scope** instead of taking it in its constructor. Steps 5–7 move inside the `using`:
```
open scope
  store = scope.GetRequiredService<IProcessedMessageStore>()
  if await store.HasBeenProcessedAsync(...) → complete, return
  await entry.Invoke(scope.ServiceProvider, ...)
  await store.MarkProcessedAsync(...)            ← the one SaveChanges
close scope
test hook check → complete
```
The handler and the store resolve the **same** DbContext instance because they share the scope. That's how their changes end up in one transaction.

**Registration**, `services.AddInbox<NotificationDbContext>()`: use `services.Replace(ServiceDescriptor.Scoped<IProcessedMessageStore, EfProcessedMessageStore<TDbContext>>())`. `AddSubscription` uses `TryAddSingleton` for the in-memory store, so with `Replace` it doesn't matter which one is called first.

**NotificationService:**
- **`Data/NotificationDbContext`** with only `MapInbox()` for now. In M4 it also stores the customer email from `OrderPlaced`.
- `AddDbContext`, the migration and `MigrateAsync` at startup.
- **That's the second copy of the migrate-with-retry code.** Move it to ServiceDefaults as `await app.MigrateDatabaseAsync<TDbContext>()`. ServiceDefaults then references `Microsoft.EntityFrameworkCore.Relational`.

**Pitfalls:**
- **`Cannot consume scoped service 'IProcessedMessageStore' from singleton`:** you left the store in the processor's constructor. In Development, DI checks lifetimes at startup, so you'll see this right away. It's the lifetime question from MESSAGING-EXPLAINED §11, Step 5.
- **A concurrent duplicate:** two instances receive the same message at once (a redelivery while the first is still working). Both pass the check, and the second insert breaks the PK, which throws a `DbUpdateException`. The whole save rolls back, so the duplicate's changes disappear. That's correct: the other instance did the work. To handle it without SQL-specific error codes: catch `DbUpdateException`, call `ChangeTracker.Clear()`, and check `AnyAsync` again. If the row exists, treat the message as a duplicate and complete it. If it doesn't, rethrow.
- **The email is still at-least-once.** Sending it is a side effect *outside* the database. If the process crashes after `EMAIL SENT` but before the save, the redelivery sends it again. No transaction can include an SMTP server. You should be able to explain this.

**Checkpoint (the milestone's "done when"):**

| Scenario | Expect |
|---|---|
| normal order | `EMAIL SENT`; one row in `NotificationDb..InboxMessages` |
| `flaky`, then `docker compose restart notification-service` right after the TEST HOOK line | after the restart: `Duplicate … skipping`. The in-memory store would have forgotten. |
| `poison` | 3 deliveries, then the DLQ; **no** inbox row (the handler threw before the save) |
| emulator stopped → POST → emulator started | 202 straight away, then `EMAIL SENT` once the emulator is back |
| `GET /orders/{id}` | `Pending` |
| `/health` on both | `Healthy` |

---

### Step 6: docs

- **README:**
  - §6c "limits of the in-memory store" and §6e "discussion, not built" → **built**
  - new §5 rows: outbox, inbox, one `SaveChanges` per message
  - §7 map: `Messaging.EntityFrameworkCore`, `Domain/`, `Data/`
  - §8: connection strings, user secrets, port 1433, the `sqlcmd` line
  - troubleshooting rows: login failed, `Cannot consume scoped service`, a migration that didn't apply
- **MESSAGING-EXPLAINED:** publishing now goes through the outbox, and consuming saves through the inbox. Add a section on each.
- **ROADMAP:** tick M3.

**Optional extras:**
- `AddDbContextCheck<TDbContext>()` (package `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`), so `/health` also reports the database.
- A relay clean-up that deletes rows sent more than a day ago.
- `GET /debug/outbox`, listing unsent rows.

---

## Don't forget

- **A new project:** add it to `EventDemo.slnx`, and add a `COPY` line for its csproj to **every** Dockerfile that needs it.
- **A model change:** `dotnet ef migrations add <Name>`, then restart the service (it migrates on startup). Commit the `Migrations/` folder.
- **Handlers never call `SaveChanges`.** The library does it once, together with the inbox row.
- **The endpoint calls `SaveChanges` exactly once,** after `Enqueue`.

## Be able to explain

- **The dual-write problem:** why "save, then publish" fails (you saw it in Step 2), and why there's no transaction that spans SQL and Service Bus.
- **Outbox vs CDC:** the outbox is a table your code writes to. Change data capture reads the database log instead.
- **Why the inbox row and the domain change share a transaction:** with two saves, a crash in between gives you either a lost message or a repeated one.
- **Why the email is still at-least-once,** and what *would* make it exactly-once (nothing: you make the receiver idempotent instead).
- **Why MessageIds must be deterministic:** a relay resend must look like the same message to the broker and to the inbox.
- **Why migrating at startup is fine now, but not in Kubernetes:** several replicas would race to migrate. M7 uses a Job instead.

## Progress

- [ ] **Step 1:** SQL on 1433, connection strings, EF packages, `dotnet-ef` tool
- [ ] **Step 2:** Order DB, `GET /orders/{id}`, dual write reproduced
- [ ] **Step 3:** `Messaging.EntityFrameworkCore`, `OutboxMessages` table, `IOutbox`
- [ ] **Step 4:** `OutboxRelay`, OrderService on the outbox, `IEventPublisher` retired, emulator-down test passes
- [ ] **Step 5:** inbox, async `IProcessedMessageStore`, NotificationDb, flaky-plus-restart test passes
- [ ] **Step 6:** docs
- [ ] Committed
