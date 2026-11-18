# EventDemo — two services talking through Azure Service Bus

> A practice project for interview prep. It's small on purpose: the aim is to _understand_ event-driven messaging well enough to explain it, not to build a real shop.

**Stack:** .NET 10 · ASP.NET Core minimal APIs · `Azure.Messaging.ServiceBus` · Azure Service Bus emulator · Docker Compose (Docker Desktop or Rancher Desktop)

---

## 1. The one-sentence version

**OrderService** takes an order over HTTP and announces _"an order was placed"_. **NotificationService** hears that announcement and reacts, for example by "sending" a confirmation email. The two services never call each other. They only talk through **Azure Service Bus**. The broker and both services run locally as Docker containers.

---

## 2. Why build it this way? (The problem messaging solves)

The obvious way to connect two services is a direct HTTP call:

```
Client ──POST /orders──▶ OrderService ──POST /send-email──▶ NotificationService
```

It works until something goes wrong:

| Problem                                                                  | What happens with a direct HTTP call                                                 |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------ |
| NotificationService is down or redeploying                               | OrderService's call fails, and the order either fails too or the email is lost.      |
| NotificationService is slow                                              | The customer waits for an email to be sent before seeing "order placed".             |
| A second service needs to know about orders (audit, analytics, shipping) | You change OrderService to call it too. OrderService ends up knowing about everyone. |
| A traffic spike                                                          | NotificationService gets hit at full speed and may fall over.                        |

Putting a **message broker** in the middle fixes each of these:

```
Client ──POST /orders──▶ OrderService ──publish──▶ [ Service Bus ] ──deliver──▶ NotificationService
                                                         │
                                                         └──deliver──▶ (any other subscriber, e.g. audit)
```

- **Temporal decoupling:** the broker _stores_ the message. If NotificationService is down, the message waits and gets processed when it comes back.
- **Fast responses:** OrderService only has to hand the message to the broker, which takes milliseconds, before replying to the customer.
- **Loose coupling:** OrderService doesn't know who's listening. Adding an audit service means adding a subscription, not changing OrderService.
- **Load levelling:** the broker absorbs spikes, and consumers work through messages at their own pace.

The price you pay is **eventual consistency** and **at-least-once delivery**. Section 6 covers both, and they're the most interesting part.

---

## 3. The pieces

```
┌─────────────────────────────── your machine ───────────────────────────────┐
│                                                                            │
│  client (.http file / curl) ──▶ localhost:5085                             │
│                                       │ port mapping 5085 → 8080           │
│  ┌─ Docker network "sb-emulator" ─────┼────────────────────────────────┐   │
│  │                                    │                                │   │
│  │  ┌───────────────────────┐         │       ┌──────────────────────┐ │   │
│  │  │ order-service         │◀────────┘       │ notification-service │ │   │
│  │  │ POST /orders          │                 │ background processor │ │   │
│  │  └──────────┬────────────┘                 └──────────▲───────────┘ │   │
│  │             │ send OrderPlacedEvent                   │ receive     │   │
│  │             │ (AMQP, sb://sb-emulator:5672)           │ (AMQP)      │   │
│  │  ┌──────────▼─────────────────────────────────────────┴───────────┐ │   │
│  │  │ servicebus-emulator                                            │ │   │
│  │  │   namespace: sbemulatorns                                      │ │   │
│  │  │     topic: orders ──┬── subscription: notifications            │ │   │
│  │  │                     └── inventory · payment · shipping (M4)    │ │   │
│  │  │   + topics inventory, payments, shipping (used from M4)        │ │   │
│  │  └────────────────────────────────────────────────────────────────┘ │   │
│  │  mssql  ← where the emulator stores messages and their state        │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────────────────────┘
```

All four pieces run as containers on one Docker network, so they reach each other **by name** (`sb-emulator`, `mssql`), not by `localhost`. Only the ports you publish (`5085`, `5289`, `5672`, `5300`) are reachable from your machine. You can also run the two services outside Docker with `dotnet run` (§8, option B); they then reach the broker through the published port `localhost:5672`.

| Piece                                        | What it is                                                            | Its job                                                                           |
| -------------------------------------------- | --------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| **Service Bus emulator**                     | A Docker container from Microsoft that behaves like Azure Service Bus | The broker: accepts, stores and delivers messages                                 |
| **mssql**                                    | SQL Server in Docker                                                  | The emulator's storage. It's why messages survive a restart                       |
| **Namespace** (`sbemulatorns`)               | The top-level container, like a "server"                              | Holds topics and queues. In Azure, this is the resource you pay for               |
| **Topics** (`orders`, `inventory`, `payments`, `shipping`) | Named channels publishers send to, **one per producing service** | `orders` receives each `OrderPlacedEvent`. The other three are ready for the services added in roadmap step 4 |
| **Subscriptions** (on `orders`: `notifications`, `inventory`, `payment`, `shipping`) | Named "inboxes" attached to a topic, one per consuming service | Each gets **its own copy** of every message sent to the topic, unless a filter narrows it |
| **Contracts**                                | A .NET class library                                                  | The 6 event shapes the services agree on, plus the `Topics` and `EventSubjects` names |
| **Messaging**                                | A .NET class library around `Azure.Messaging.ServiceBus`              | All broker plumbing: publishing with a standard envelope, and consuming (validate, dispatch by Subject, dedupe, complete or dead-letter). Services only write handlers |
| **ServiceDefaults**                          | A .NET class library                                                  | Setup every service shares. For now `/health`; OpenTelemetry later               |
| **OrderService**                             | ASP.NET Core minimal API, container `order-service`                   | The **publisher (producer)**                                                      |
| **NotificationService**                      | ASP.NET Core app with a background worker, container `notification-service` | The **subscriber (consumer)**                                               |

### Topic vs queue

- **Queue:** one message goes to exactly _one_ receiver. Use it for **commands** ("send this email"), where one handler does the work.
- **Topic + subscriptions:** one message is copied to _every_ subscription. Use it for **events** ("an order was placed"), where any number of services may care.

We use a topic because `OrderPlaced` is an event. Each subscription is an independent queue with its own locks, retries and dead-letter queue, so one consumer failing doesn't affect the others.

Inside a single subscription, each message is still handled once. If you run several instances of NotificationService on the same subscription, they _share_ it and split the work between them (the **competing consumers** pattern).

A subscription only receives messages sent **after** it exists. By default it receives everything (the `$Default` rule, `TrueRuleFilter`), but it can be given **filters** on message properties. `Config.json` uses **correlation filters on `Label`** (the AMQP name for Subject): for example, `payments/shipping` only receives `PaymentSucceeded`, and `payments/inventory` only `PaymentFailed`. A correlation filter is an exact match the broker can index; a SQL filter (`Currency = 'EUR'`) is evaluated per message.

**Why one topic per producer:** each topic has one owner, the service that publishes to it. Consumers pick what they need with their own subscription. The emulator caps duplicate detection at 5 minutes (`PT5M`); real Azure allows up to 7 days.

---

## 4. The life of one order, step by step

### Publishing (OrderService)

1. A client sends `POST /orders` with a JSON body: product, customer, email, addresses, amount and currency. This is the `PlaceOrderRequestDTO` (the **API contract**).
2. OrderService generates the parts the client shouldn't control: the `OrderId` (`Guid.CreateVersion7()`, which is time-ordered) and `PlacedAt`.
3. It builds an `OrderPlacedEvent` (the **event contract**, from the Contracts library). The event carries only what consumers need, not the whole request.
4. It calls `IEventPublisher.PublishAsync(event, EventSubjects.OrderPlaced, messageId: OrderId, correlationId: OrderId)` from the **Messaging** library. Messaging's `MessageEnvelope` wraps the event in a `ServiceBusMessage`, the same way for every service:
   - **Body:** the event serialized to JSON.
   - **Envelope properties:** metadata _about_ the message, which the broker and consumers can read without opening the body.
     - `MessageId = OrderId`: stable across retries, so duplicates can be recognized.
     - `Subject`: `"OrderPlaced"`, the type of the event. Subscription filters and the consumer's handler lookup both match on it.
     - `ContentType`: `"application/json"`.
     - `CorrelationId = OrderId`: every event about this order, in every service, carries the same value, so you can follow one order through the system.
     - `ApplicationProperties["SchemaVersion"] = 1`: which version of the event shape this is.
   - The SDK also adds a `Diagnostic-Id` property by itself: a W3C trace context that tools like OpenTelemetry use to link the HTTP request and the message handling into one trace. That's tracing; `CorrelationId` is the business key.
   - The envelope refuses an empty `MessageId`, `CorrelationId` or `Subject`, so a missing id fails at the sender instead of quietly defeating duplicate detection.
5. The publisher calls `SendMessageAsync`. When that returns, **the broker has stored the message durably**. That's the guarantee OrderService relies on.
6. OrderService returns **202 Accepted** with the `orderId`: "I've accepted your order and the rest will happen". It doesn't return 201 Created, because the downstream work hasn't happened yet.

### In the broker

7. The topic `orders` gets the message and puts a copy in **each** subscription: `notifications`, `inventory`, `payment` and `shipping`. Only `notifications` has a consumer so far; the other three copies wait for the services added in roadmap step 4.
8. Each copy sits in its subscription in the **Active** state, waiting. It stays there whether or not a consumer is running, until it's consumed or expires (time to live is 1 hour in `Config.json`).

### Consuming (NotificationService)

9. NotificationService registers one subscription and one handler:
   ```csharp
   builder.Services.AddServiceBus(connectionString)
       .AddSubscription(Topics.Orders, "notifications")
       .Handle<OrderPlacedEvent, OrderPlacedHandler>(EventSubjects.OrderPlaced);
   ```
   For each `AddSubscription`, Messaging runs a `SubscriptionProcessor`: a `BackgroundService` around a `ServiceBusProcessor`. It keeps an open connection and asks for messages from `orders/notifications`.
10. The broker hands over a message in **peek-lock** mode. The message isn't deleted. It's **locked** to this consumer for 30 seconds (`LockDuration` in `Config.json`), so no other consumer can take it.
11. The `SubscriptionProcessor`:
    1. looks up the handler registered for the message's `Subject`, and checks `SchemaVersion`. Anything unexpected is **dead-lettered at once** (`UnknownSubject`, `InvalidMessage`): it's a permanent failure, so retrying it is pointless.
    2. deserializes the body to the type registered for that subject. Invalid JSON is also dead-lettered at once (`DeserializationFailed`).
    3. checks the **processed-message store**, keyed by `subscription:MessageId`. If it was already handled, it logs `Duplicate … skipping` and completes the message without doing the work again.
    4. calls the handler, `OrderPlacedHandler`, in its own DI scope. The handler does the work: here that's logging `EMAIL SENT for order {OrderId}`. If the handler throws, the processor **abandons** the message and it's retried.
    5. records the message as processed.
12. Finally the processor calls **`CompleteMessageAsync`**. Only now does the broker delete the message.

The handler contains only business logic. Everything in steps 11.1–11.3, 11.5 and 12 is the same for every service, so it's written once, in Messaging.

```
Active ──(receive)──▶ Locked ──(complete)──▶ deleted ✅
                        │
                        ├──(handler throws → processor abandons)──▶ Active again at once, DeliveryCount + 1
                        ├──(no response, e.g. crash → lock expires)─▶ Active again after 30 s, DeliveryCount + 1
                        ├──(handler dead-letters: permanent failure)──▶ Dead-letter queue ☠️
                        └──(DeliveryCount reaches MaxDeliveryCount = 3)──▶ Dead-letter queue ☠️
```

---

## 5. Why the code is shaped the way it is

| Decision                                                                                          | Reason                                                                                                                                                                     |
| ------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ServiceBusClient` and `ServiceBusSender` are **singletons**                                      | The client owns one long-lived AMQP connection (TCP, TLS, authentication) and is thread-safe. Creating one per request adds handshake latency and can exhaust connections. |
| A `PlaceOrderRequestDTO` **separate** from `OrderPlacedEvent`                                     | The public API and the event change for different reasons and are used by different people. Keeping them apart means one can change without breaking the other.            |
| The server sets `OrderId` and `PlacedAt`                                                          | The client shouldn't decide ids, timestamps or statuses.                                                                                                                   |
| `MessageId = OrderId`                                                                             | A stable id means the same order is recognized as a duplicate, whether it comes from a sender retry or a redelivery. A random GUID per attempt would defeat that.          |
| Envelope data in message properties, **not in the body**                                          | Brokers and consumers can filter, route and trace on properties without deserializing the body.                                                                            |
| `required` properties on the event                                                                | A missing field fails loudly instead of quietly becoming `""` or "now".                                                                                                    |
| The publisher doesn't know any subscription names                                                 | That's the point of a topic: the producer is decoupled from its consumers.                                                                                                 |
| `AutoCompleteMessages = false` in the consumer                                                    | You decide when a message counts as handled: only after the work actually succeeded.                                                                                       |
| Permanent failures are dead-lettered **immediately**                                              | A bad subject, an unknown schema version or invalid JSON fails the same way every time. Only transient failures deserve retries.                                           |
| The processed-message store is checked **before** the work and written **after** it               | A crash between the work and the write causes a duplicate, but never a lost email.                                                                                         |
| Duplicates are still **completed**                                                                | Otherwise the lock would expire, the message would come back, and it would end up in the DLQ as if it had failed.                                                          |
| `StopProcessingAsync` runs before `base.StopAsync`                                                | On shutdown (Ctrl+C), the message currently being handled finishes cleanly.                                                                                                |
| Broker plumbing lives in a shared **Messaging** library; services only implement `IEventHandler<TEvent>` | Validation, dedupe and complete/dead-letter are identical in every consumer. Written once, they're fixed once. The library holds **plumbing only**, never business rules, because every service depends on its version. |
| Messaging does **not** reference Contracts                                                        | The library works for any event type. It never needs a rebuild when an event changes, and business types can't creep into it.                                              |
| Handlers are found **by Subject**                                                                 | One subscription can carry several event types (in step 4, OrderService's `inventory` subscription gets both `StockReserved` and `StockReservationFailed`).                 |
| Each registration captures its event and handler types in two lambdas                             | The processor deserializes and calls the right handler without reflection, and `Handle<TEvent, THandler>()` won't compile if the handler doesn't match the event.          |
| **One DI scope per message**                                                                      | Each message is its own unit of work. In step 3 the scope holds a `DbContext`, which must never be shared between messages.                                                 |
| The processor never catches the handler's exceptions                                              | Throwing is how a handler says "try again later": the processor abandons the message, and the broker counts the attempt toward `MaxDeliveryCount`.                         |
| Processors are registered with `AddSingleton<IHostedService>(factory)`, not `AddHostedService<T>()` | `AddHostedService` silently ignores a second registration of the same type, so with several subscriptions only the first would run.                                   |
| The sender is created inside the publisher's factory and never registered on its own              | Nothing in a service can inject a bare `ServiceBusSender` and skip the envelope (ids, Subject, SchemaVersion).                                                              |
| Logs contain `OrderId`, `MessageId`, `DeliveryCount` and `CorrelationId`, **not the whole event** | The event contains an email address. Personal data doesn't belong in logs.                                                                                                 |
| Connection string in **user secrets**                                                             | The emulator key isn't really secret, but a real Azure key is. Build the habit now.                                                                                        |
| `nuget.config` in the repo                                                                        | Clears any machine-wide package source mapping and maps every package to nuget.org, so the repo builds the same on any machine.                                            |

---

## 6. What happens when things go wrong (milestone 5)

Most of the learning is here. Each scenario was run against the emulator. The `poison` and `flaky` customer IDs are **test hooks** that trigger the failures (see §8).

### a) The consumer is offline → messages wait

An order posted while NotificationService was stopped was processed the moment it started again.
**Lesson:** the broker stores messages, so producer and consumer don't need to be up at the same time (**temporal decoupling**).

### b) The handler keeps failing → dead-letter queue

A `poison` order makes the handler throw. The processor **abandons** the message each time, so it's redelivered **immediately**. After 3 attempts it moves to the **dead-letter queue (DLQ)**, at `orders/subscriptions/notifications/$deadletterqueue`, with `DeadLetterReason = MaxDeliveryCountExceeded`. A normal order sent right after it was still processed.

Leaving a message without completing it behaves differently, as an earlier experiment showed. The broker hears nothing, so it waits for the **30-second lock to expire** before each redelivery.

| What happens in the consumer                                       | Broker's view                                   | Redelivered                 |
| ------------------------------------------------------------------ | ----------------------------------------------- | --------------------------- |
| The handler **throws**                                             | The processor sends an explicit **Abandon**     | Immediately                 |
| The handler returns without completing, **or the process crashes** | No response, so it waits for the lock to expire | After `LockDuration` (30 s) |

**Lessons:**

- A bad message doesn't block the others or retry forever. Someone inspects the DLQ (`GET /debug/peek-dlq/orders/notifications`), fixes the cause and resubmits.
- Service Bus has **no built-in delay between retries**. Transient failures need your own backoff, for example by rescheduling the message.
- Resubmitting a DLQ message to the _topic_ would deliver it to **every** subscription again, not just the one where it failed.

### c) A crash after the work but before completing → duplicates → idempotency

A `flaky` order does the work ("EMAIL SENT") and then deliberately doesn't complete on its first delivery. After the lock expires, the message comes back with `DeliveryCount = 2`. Without protection, the email would be sent twice. With the processed-message store, the log shows `Duplicate … (delivery 2), skipping`.
**Lesson:** Service Bus gives **at-least-once** delivery, not exactly-once, so consumers must be **idempotent**.

**Why the broker's duplicate detection doesn't solve this:**

| Where the duplicate comes from | What causes it                                        | What prevents it                                                                                                 |
| ------------------------------ | ----------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| **Sender:** a retried send     | A timeout or network glitch during `SendMessageAsync` | Broker **duplicate detection** (`RequiresDuplicateDetection`) within its time window, using a stable `MessageId` |
| **Receiver:** redelivery       | A crash or lost lock before `Complete`                | **An idempotent consumer**                                                                                       |

A redelivery isn't a new send. It's the same message delivered again, so duplicate detection never sees it. In practice you use both.

**Limits of the in-memory store (demo only):**

- It's empty after a restart, so a duplicate after a _real_ crash would still get through.
- It isn't shared between instances. With competing consumers, a redelivery can land on a **different instance** that has never seen the message.
- Production uses a **database table with a unique constraint on `MessageId`**, shared by all instances of the service and ideally written in the same transaction as the work (the **inbox pattern**).

**Services vs instances:** _different services_ (for example NotificationService and, in step 4, InventoryService) each keep their own processed list, because each must handle every message. _Instances of the same service_ must share one store, because any instance may receive any message, including a redelivery.

### d) A second subscriber → fan-out

First this was shown with an extra `audit` subscription. With the topic-per-producer layout, `orders` now has four subscriptions (`notifications`, `inventory`, `payment`, `shipping`), one per service that cares about new orders. Stop notification-service, POST one order, and peek all four in `OrderService.http`: the **same MessageId** is waiting in each. Three of them wait for services that don't exist yet, and **OrderService didn't change at all**.
**Lesson:** this is the loose coupling a topic gives you. Several instances on the _same_ subscription instead **split** the messages between them (competing consumers), which is how you scale out.

### e) The dual-write problem (discussion, not built)

A real OrderService would _save the order to a database_ and _publish the event_. Those are two separate systems with no shared transaction:

- If the save succeeds and the publish fails (or the process crashes in between), the order exists but nobody hears about it.
- If you publish first and the save fails, other services react to an order that doesn't exist.

Waiting for an acknowledgement doesn't help: `SendMessageAsync` already _is_ the broker's acknowledgement.

**The fix is the transactional outbox:** in **one** database transaction, save the order **and** an "outbox" row containing the event. A background relay reads unsent outbox rows, publishes them and marks them as sent, retrying until it succeeds. The relay can publish a row twice, so delivery is at-least-once: broker duplicate detection and idempotent consumers absorb the repeats. Libraries such as MassTransit and NServiceBus implement the outbox for you; change data capture (CDC) is an alternative.

---

## 7. Repository map

```
.
├─ README.md                      ← this file
├─ EventDemo.slnx                 ← solution file (.NET 10's XML format); groups the 5 projects
├─ global.json                    ← pins the .NET SDK to 10.x
├─ nuget.config                   ← maps every package to nuget.org
├─ .gitignore                     ← standard .NET ignores, plus infra/.env
├─ .dockerignore                  ← keeps bin/, obj/, .git and infra/.env out of image builds
├─ docs\
│  ├─ ROADMAP.md                  ← next phase: 5 microservices, outbox/inbox, tracing, Kubernetes
│  └─ MILESTONE-2.md              ← step-by-step guide and progress for roadmap step 2
│
├─ infra\                         ← everything needed to run the system locally
│  ├─ docker-compose.yaml         ← four containers: mssql, servicebus-emulator, order-service, notification-service
│  ├─ Config.json                 ← namespace, 4 topics (one per producer), 13 subscriptions, filters
│  └─ .env                        ← SQL password + EULA flag (NOT committed)
│
└─ src\
   ├─ Contracts\                  ← what the services agree on
   │  ├─ OrderPlacedEvent.cs, StockReservedEvent.cs, …  ← the 6 event shapes
   │  ├─ Topics.cs                ← topic names
   │  └─ EventSubjects.cs         ← Subject of each event; Config.json filters match these exactly
   │
   ├─ Messaging\                  ← SHARED: all broker plumbing (no business logic, no Contracts reference)
   │  ├─ ServiceCollectionExtensions.cs ← the entry point: AddServiceBus, AddEventPublisher, AddSubscription
   │  ├─ Publishing\
   │  │  ├─ MessageEnvelope.cs    ← builds every ServiceBusMessage: ids, Subject, ContentType, SchemaVersion
   │  │  └─ IEventPublisher.cs, ServiceBusEventPublisher.cs  ← publishing to the service's own topic
   │  ├─ Consuming\
   │  │  ├─ IEventHandler.cs, MessageContext.cs  ← what a service implements to consume
   │  │  ├─ SubscriptionRegistration.cs  ← AddSubscription(...).Handle<TEvent, THandler>(subject)
   │  │  └─ SubscriptionProcessor.cs  ← BackgroundService per subscription: validate, dispatch, dedupe, complete / dead-letter
   │  ├─ Idempotency\
   │  │  ├─ IProcessedMessageStore.cs  ← "was this message already handled?"
   │  │  └─ InMemoryProcessedMessageStore.cs  ← the demo implementation (lost on restart)
   │  └─ Diagnostics\
   │     └─ DebugEndpoints.cs     ← GET /debug/peek/{topic}/{sub}, /debug/peek-dlq/{topic}/{sub}
   │
   ├─ ServiceDefaults\            ← SHARED: AddServiceDefaults() / MapDefaultEndpoints() → /health
   │
   ├─ OrderService\               ← PUBLISHER
   │  ├─ Dockerfile               ← multi-stage build (SDK → ASP.NET runtime), runs as non-root on :8080
   │  ├─ Program.cs               ← AddServiceBus + AddEventPublisher(Topics.Orders), POST /orders, debug endpoints (Development)
   │  ├─ DTOs\
   │  │  └─ PlaceOrderRequestDTO.cs  ← the API contract (what a client sends)
   │  └─ OrderService.http        ← requests to send from VS Code (REST Client extension)
   │
   └─ NotificationService\        ← SUBSCRIBER
      ├─ Dockerfile               ← same pattern as OrderService's
      ├─ Program.cs               ← AddServiceBus + AddSubscription(orders, notifications).Handle<…>
      └─ OrderPlacedHandler.cs    ← the business part only: EMAIL SENT, plus the poison and flaky test hooks
```

Both services read the **connection string** from the config key `ServiceBus:ConnectionString`. Where the value comes from depends on how the service runs:

- **In a container:** from the environment variable `SERVICEBUS__CONNECTIONSTRING` set in `docker-compose.yaml`. .NET turns `__` into `:` when it loads environment variables, so the code always asks for `ServiceBus:ConnectionString`, never for the `__` form.
- **With `dotnet run`:** from **user secrets**, stored under your user profile and linked to each project by the `UserSecretsId` in its `.csproj`. Containers can't see user secrets.

The emulator's key (`SAS_KEY_VALUE`) is public and documented, which is why it can sit in the compose file. A real Azure key would come from a secret store instead.

---

## 8. How to run it

There are two ways to run it:

- **A: everything in containers.** This is the normal way. You only need Docker.
- **B: the broker in containers, the services with `dotnet run`.** Use this when you want to debug the C# code with breakpoints.

### Prerequisites

- Docker Desktop (on Windows, with the WSL 2 engine), or Rancher Desktop with the **dockerd (moby)** container engine. With containerd, `docker` commands don't work; use `nerdctl compose` instead
- .NET 10 SDK, only for option B. Option A builds inside the `sdk:10.0` image
- VS Code with the **REST Client** extension, to use `OrderService.http` (optional)

> **Rancher Desktop:** if `docker` isn't recognized in a VS Code terminal, restart VS Code. Rancher adds its tools to `PATH`, and terminals opened before the install don't see the change.

### 1. Create `infra/.env` (once)

```
CONFIG_PATH="./Config.json"
ACCEPT_EULA="Y"
MSSQL_SA_PASSWORD="<strong password: 8+ chars, upper/lower/digit/symbol, no ; = quotes or $>"
EMULATOR_HTTP_PORT=5300
SQL_WAIT_INTERVAL=15
```

`ACCEPT_EULA="Y"` accepts the license terms of the Service Bus emulator and SQL Server. The password ends up inside a SQL connection string, so it must not contain `;`. `SQL_WAIT_INTERVAL` is optional; setting it silences a compose warning.

### 2A. Start everything in containers

From `infra\`:

```powershell
docker compose up -d --build
docker compose ps                         # 4 containers running
curl.exe http://localhost:5300/health     # 200 once the emulator is ready (~30 s)
```

| Container              | From your machine                       | What it is                               |
| ---------------------- | --------------------------------------- | ---------------------------------------- |
| `order-service`        | http://localhost:5085 (→ 8080 inside)   | The publisher: `POST /orders`, `/debug/*`, `/health` |
| `notification-service` | http://localhost:5289 (→ 8080 inside)   | The consumer. Only `/health` over HTTP   |
| `servicebus-emulator`  | `localhost:5672` (AMQP), `5300` (health) | The broker                              |
| `mssql`                | not published                           | The emulator's storage                   |

Watch a service's log with `docker compose logs -f notification-service`, or `order-service`.

**What to rerun after a change:**

| You changed                   | Run                                              | Why                                                                                         |
| ----------------------------- | ------------------------------------------------ | ------------------------------------------------------------------------------------------- |
| C# code                       | `docker compose up -d --build <service>`         | The code is baked into the image. Without `--build` you keep running the old image          |
| `.env` or `Config.json`       | `docker compose up -d --force-recreate`          | Docker reads them only when it creates a container. This also **empties the broker**        |
| `docker-compose.yaml`         | `docker compose up -d`                           | Compose recreates only the containers whose settings changed                                |

**PowerShell:** use `curl.exe`, not `curl`. In PowerShell, `curl` is an alias for `Invoke-WebRequest`.

### 2B. Or run the services with `dotnet run`

Start only the broker, or stop the service containers if they're running. Otherwise ports 5085 and 5289 clash, and a second NotificationService would quietly share the subscription with the first:

```powershell
docker compose up -d mssql emulator
docker compose stop order-service notification-service   # if they were running
```

Set the connection string once per project. Outside Docker the broker is at `localhost`:

```bash
dotnet user-secrets set "ServiceBus:ConnectionString" "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;" --project src/OrderService
dotnet user-secrets set "ServiceBus:ConnectionString" "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;" --project src/NotificationService
```

`SAS_KEY_VALUE` is the emulator's literal, documented key. Don't replace it.

Then, each in its own terminal, from the repo root:

```bash
dotnet run --project src/OrderService          # http://localhost:5085
dotnet run --project src/NotificationService   # http://localhost:5291 (subscription: notifications)
```

Stop them with **Ctrl+C**, so the consumer shuts down gracefully.

**Optional: competing consumers.** A second NotificationService next to the first (container or `dotnet run`) shares `orders/notifications` with it, and the two split the messages. It works in either setup, because it reaches the broker through `localhost:5672`:

```bash
dotnet run --project src/NotificationService -- --urls http://localhost:5293
```

The old `--ServiceBus:SubscriptionName=audit` trick is gone: the `audit` subscription was removed, and the subscription name is now in code (`AddSubscription(Topics.Orders, "notifications")`). Fan-out is shown with the peek requests instead (§6d).

### 3. Send requests

Open `src/OrderService/OrderService.http` and click **Send Request**, or use curl. Both setups use http://localhost:5085.

| Request                                      | What it does                                                                                                          |
| -------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| `POST /orders`                               | Places a normal order; returns 202 with the `orderId`                                                                 |
| `POST /orders` with `"customerId": "poison"` | **Test hook:** the handler always throws → 3 immediate retries → DLQ                                                  |
| `POST /orders` with `"customerId": "flaky"`  | **Test hook:** the work runs, but the first delivery isn't completed → redelivery after 30 s → `Duplicate … skipping` |
| `GET /debug/peek/{topic}/{subscription}`     | Lists up to 10 messages waiting in any subscription, e.g. `orders/inventory`, **without** consuming them. The body is the raw JSON |
| `GET /debug/peek-dlq/{topic}/{subscription}` | Lists up to 10 dead-lettered messages with `DeadLetterReason`, `DeadLetterErrorDescription` and `DeliveryCount`        |
| `GET /health` (5085 and 5289)                | `Healthy` when the service is up (liveness only for now)                                                              |

The `/debug/*` endpoints and the test hooks exist for learning only. The debug endpoints are mapped **only in Development**, which is why compose sets `ASPNETCORE_ENVIRONMENT: Development` on both services.

**Consumer offline (temporal decoupling) with containers:**

```powershell
docker compose stop notification-service
# POST an order → 202; GET /debug/peek/orders/notifications shows it waiting
docker compose start notification-service
docker compose logs -f notification-service    # the order is processed on startup
```

### 4. Stop

```powershell
docker compose stop    # stops the containers; messages in the broker are kept
docker compose down    # removes the containers; the broker starts empty next time
```

### Troubleshooting

| Symptom                                                                       | Cause                                                                                                                                                                                                   |
| ----------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SocketException (10061)` / "target machine actively refused it"              | The emulator isn't running or is restarting. Check `docker compose ps` and `docker compose logs emulator`. Right after startup, wait ~30 s.                                                              |
| `ArgumentNullException … (Parameter 'connectionString')` on the first request | The connection string didn't reach the app. In compose, the variable must be `SERVICEBUS__CONNECTIONSTRING` (**double** underscore), and the code must read `ServiceBus:ConnectionString` (colon). Check with `docker compose exec order-service printenv SERVICEBUS__CONNECTIONSTRING`. |
| A container can't reach the broker, but `dotnet run` can                      | The connection string inside the container says `localhost`, which there means the container itself. Use `sb://sb-emulator`.                                                                             |
| Connection hangs or fails even with the right host                            | `UseDevelopmentEmulator=true` is missing. Without it, the SDK tries TLS on port 5671, which the emulator doesn't serve.                                                                                  |
| A code change doesn't show up in the container                                | The image wasn't rebuilt. Use `docker compose up -d --build <service>`.                                                                                                                                 |
| `Bind for 0.0.0.0:5085 failed: port is already allocated`                     | A `dotnet run` copy of the service is still running. Stop it, or stop the container (option B).                                                                                                         |
| `services.<name>.<other> … schema` when running compose                       | A YAML indentation mistake: a service is nested inside another. Every service must start at the same column. Check with `docker compose config --services`.                                              |
| `Failed to determine the https port for redirect` in a container log          | `UseHttpsRedirection()` is running in the container, which only serves HTTP on 8080. This project doesn't use it.                                                                                       |
| Emulator keeps restarting; log says `Keyword not supported`                   | The SQL password contains `;`.                                                                                                                                                                          |
| Emulator exits; log says `Max Duplicate detection window supported 5m`        | A topic in `Config.json` has `DuplicateDetectionHistoryTimeWindow` above `PT5M`. The emulator's limit is 5 minutes (real Azure allows 7 days).                                                         |
| `/debug/...` returns **404** in a container                                   | The service isn't running in Development, where the debug endpoints are mapped. Check `ASPNETCORE_ENVIRONMENT: Development` in `docker-compose.yaml`, then `docker compose up -d`.                     |
| Startup fails: `Subject '…' already has a handler on …`                       | Two `.Handle<…>()` calls on one subscription use the same subject. Each subject needs exactly one handler per subscription.                                                                           |
| `NU1100` / "PackageSourceMapping is enabled"                                  | A global NuGet config is blocking packages. The repo's `nuget.config` fixes that; make sure it's present.                                                                                               |
| Build error "file is locked by OrderService"                                  | A service is still running under `dotnet run`. Stop it, then build.                                                                                                                                     |
| `/debug/peek/...` is empty right after a POST                                 | The emulator takes a moment to make a new message readable. Try again after a second or two.                                                                                                            |

---

## 9. Progress

- [x] **Milestone 1: environment.** Emulator and SQL running in Docker, health check passing.
- [x] **Milestone 2: skeleton.** Solution, 3 projects, references, packages, config, secrets, event record.
- [x] **Milestone 3: publisher.** `POST /orders` publishes `OrderPlacedEvent` with envelope properties; `GET /debug/peek` shows it without consuming it.
- [x] **Milestone 4: consumer.** NotificationService validates, processes and completes each order; permanent failures are dead-lettered.
- [x] **Milestone 5: break it.**
  - [x] a) Offline consumer: messages wait and are processed later
  - [x] b) Poison message: immediate retries, then the DLQ (`GET /debug/peek-dlq`)
  - [x] c) Duplicates: redelivery after the lock expires; idempotent consumer on `MessageId`
  - [x] d) Fan-out to `audit` (subscription added; run both consumers) and competing consumers
- [ ] **Milestone 6 (optional): real Azure.** Standard namespace (topics need Standard tier), swap the connection string, try `DefaultAzureCredential`.
- [ ] **Milestone 7 (optional): transactional outbox.** Store orders in SQL and publish through an outbox relay.

### Next phase: microservices ([docs/ROADMAP.md](docs/ROADMAP.md))

- [x] **Step 1: containers.** Both services run as containers next to the broker (a Dockerfile per service and one compose file).
- [x] **Step 2: shared libraries and the new topology** ([docs/MILESTONE-2.md](docs/MILESTONE-2.md)). Contracts for all 6 events, one topic per producer with filtered subscriptions, `ServiceDefaults` (`/health`), and a `Messaging` library that does all publishing and consuming. Both services run on it, and all M1 scenarios still pass.
- [ ] Step 3: a database per service, the outbox and the inbox (this replaces Milestone 7 above)
- [ ] Step 4: Inventory, Payment and Shipping services: a choreographed saga with compensation
- [ ] Step 5: OpenTelemetry and the Aspire Dashboard
- [ ] Step 6: README rewrite
- [ ] Step 7: Kubernetes (planned only)

---

## 10. Glossary

| Term                               | Meaning                                                                                                                   |
| ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| **Broker**                         | The middleman that stores and delivers messages (Service Bus here)                                                        |
| **Producer / publisher**           | Sends messages (OrderService)                                                                                             |
| **Consumer / subscriber**          | Receives and handles messages (NotificationService)                                                                       |
| **Event**                          | A fact about the past ("OrderPlaced"). Owned by the producer, 0..n consumers, sent to a topic                             |
| **Command**                        | A request to do something ("SendEmail"). Owned by the receiver, exactly 1 handler, sent to a queue                        |
| **AMQP 1.0**                       | The open wire protocol Service Bus uses (port 5672); a long-lived binary connection                                       |
| **Peek**                           | Look at messages without locking or removing them                                                                         |
| **Peek-lock**                      | Receive a message under a temporary lock; it's deleted only when you _complete_ it                                        |
| **Receive-and-delete**             | The message is deleted as soon as it's handed out. Faster, but lost if you crash                                          |
| **Complete / Abandon**             | Tell the broker "done, delete it" / "I couldn't handle it, give it back"                                                  |
| **Lock duration**                  | How long a received message stays invisible to others (30 seconds here)                                                   |
| **Delivery count**                 | How many times the message has been handed out                                                                            |
| **Poison message**                 | A message that fails every time it's processed                                                                            |
| **Dead-letter queue (DLQ)**        | Where messages go after too many failed attempts, when they expire, or when a consumer dead-letters them                  |
| **Transient vs permanent failure** | Might succeed on retry (timeout, database briefly down) vs will fail every time (bad data). Only retry transient failures |
| **At-least-once**                  | Every message is delivered one or more times, so duplicates are possible                                                  |
| **Idempotent**                     | Processing the same message twice has the same effect as processing it once                                               |
| **Duplicate detection**            | A broker feature that drops a _sent_ message whose `MessageId` it saw within a time window                                |
| **Inbox pattern**                  | Record processed message ids in the consumer's database, in the same transaction as the work                              |
| **Fan-out**                        | One published message reaching many subscribers                                                                           |
| **Competing consumers**            | Several instances sharing one subscription and splitting the work                                                         |
| **Eventual consistency**           | The other services catch up shortly after, not instantly                                                                  |
| **Transactional outbox**           | Save the data and the outgoing event in one DB transaction; publish separately                                            |
| **Tolerant reader**                | A consumer that ignores fields it doesn't know, so additive changes are safe                                              |

---
