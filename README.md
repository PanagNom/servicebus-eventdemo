# EventDemo — two services talking through Azure Service Bus

> A practice project for interview prep. It's small on purpose: the aim is to _understand_ event-driven messaging well enough to explain it, not to build a real shop.

**Stack:** .NET 10 · ASP.NET Core minimal APIs · `Azure.Messaging.ServiceBus` · Azure Service Bus emulator (Docker)

---

## 1. The one-sentence version

**OrderService** takes an order over HTTP and announces _"an order was placed"_. **NotificationService** hears that announcement and reacts, for example by "sending" a confirmation email. The two services never call each other. They only talk through **Azure Service Bus**, which runs locally in Docker.

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
┌──────────────────────────── your machine ─────────────────────────────┐
│                                                                        │
│  ┌──────────────┐                                ┌───────────────────┐ │
│  │ OrderService │                                │NotificationService│ │
│  │  (web API)   │                                │  (background      │ │
│  │ POST /orders │                                │   processor)      │ │
│  └──────┬───────┘                                └─────────▲─────────┘ │
│         │ send OrderPlacedEvent                           │ receive    │
│         │ (AMQP, port 5672)                               │ (AMQP)     │
│  ┌──────▼──────────────── Docker ────────────────────────┴─────────┐  │
│  │  servicebus-emulator                                             │  │
│  │    namespace: sbemulatorns                                       │  │
│  │      topic: orders ──┬── subscription: notifications ───────────┘  │
│  │                      └── subscription: audit ──▶ 2nd consumer      │
│  │                                                                     │
│  │  mssql  ← where the emulator stores messages and their state       │
│  └─────────────────────────────────────────────────────────────────────┘
└────────────────────────────────────────────────────────────────────────┘
```

| Piece                                        | What it is                                                            | Its job                                                                           |
| -------------------------------------------- | --------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| **Service Bus emulator**                     | A Docker container from Microsoft that behaves like Azure Service Bus | The broker: accepts, stores and delivers messages                                 |
| **mssql**                                    | SQL Server in Docker                                                  | The emulator's storage. It's why messages survive a restart                       |
| **Namespace** (`sbemulatorns`)               | The top-level container, like a "server"                              | Holds topics and queues. In Azure, this is the resource you pay for               |
| **Topic** (`orders`)                         | A named channel publishers send to                                    | Receives each `OrderPlacedEvent`                                                  |
| **Subscriptions** (`notifications`, `audit`) | Named "inboxes" attached to a topic                                   | Each gets **its own copy** of every message sent to the topic                     |
| **Contracts**                                | A .NET class library                                                  | Defines the shape of the message (`OrderPlacedEvent`) that both services agree on |
| **OrderService**                             | ASP.NET Core minimal API                                              | The **publisher (producer)**                                                      |
| **NotificationService**                      | ASP.NET Core app with a background worker                             | The **subscriber (consumer)**                                                     |

### Topic vs queue

- **Queue:** one message goes to exactly _one_ receiver. Use it for **commands** ("send this email"), where one handler does the work.
- **Topic + subscriptions:** one message is copied to _every_ subscription. Use it for **events** ("an order was placed"), where any number of services may care.

We use a topic because `OrderPlaced` is an event. Each subscription is an independent queue with its own locks, retries and dead-letter queue, so one consumer failing doesn't affect the others.

Inside a single subscription, each message is still handled once. If you run several instances of NotificationService on the same subscription, they _share_ it and split the work between them (the **competing consumers** pattern).

A subscription only receives messages sent **after** it exists. By default it receives everything (the `$Default` rule, `TrueRuleFilter`), but it can be given **filters** on message properties, for example "only `Currency = 'EUR'`".

---

## 4. The life of one order, step by step

### Publishing (OrderService)

1. A client sends `POST /orders` with a JSON body: product, customer, email, addresses, amount and currency. This is the `PlaceOrderRequestDTO` (the **API contract**).
2. OrderService generates the parts the client shouldn't control: the `OrderId` (`Guid.CreateVersion7()`, which is time-ordered) and `PlacedAt`.
3. It builds an `OrderPlacedEvent` (the **event contract**, from the Contracts library). The event carries only what consumers need, not the whole request.
4. It wraps the event in a `ServiceBusMessage`:
   - **Body:** the event serialized to JSON.
   - **Envelope properties:** metadata _about_ the message, which the broker and consumers can read without opening the body.
     - `MessageId = OrderId`: stable across retries, so duplicates can be recognized.
     - `Subject`: `"OrderPlaced"`, the type of the event.
     - `ContentType`: `"application/json"`.
     - `CorrelationId`: the HTTP request's `TraceIdentifier`, which ties the message to the request that caused it.
     - `ApplicationProperties["SchemaVersion"] = 1`: which version of the event shape this is.
   - The SDK also adds a `Diagnostic-Id` property by itself: a W3C trace context that tools like OpenTelemetry use to link the HTTP request and the message handling into one trace.
5. It calls `SendMessageAsync`. When that returns, **the broker has stored the message durably**. That's the guarantee OrderService relies on.
6. OrderService returns **202 Accepted** with the `orderId`: "I've accepted your order and the rest will happen". It doesn't return 201 Created, because the downstream work hasn't happened yet.

### In the broker

7. The topic `orders` gets the message and puts a copy in **each** subscription: `notifications` and `audit`.
8. Each copy sits in its subscription in the **Active** state, waiting. It stays there whether or not a consumer is running, until it's consumed or expires (time to live is 1 hour in `Config.json`).

### Consuming (NotificationService)

9. NotificationService runs a `ServiceBusProcessor` inside a `BackgroundService` (`OrderPlacedConsumer`). It keeps an open connection and asks for messages from its subscription (`notifications` by default).
10. The broker hands over a message in **peek-lock** mode. The message isn't deleted. It's **locked** to this consumer for 30 seconds (`LockDuration` in `Config.json`), so no other consumer can take it.
11. The handler:
    1. checks `Subject` and `SchemaVersion`. Anything unexpected is **dead-lettered at once**: it's a permanent failure, so retrying it is pointless.
    2. deserializes the body. Invalid JSON is also dead-lettered at once.
    3. checks the **processed-message store**. If this `MessageId` was already handled, it logs `Duplicate … skipping` and completes the message without doing the work again.
    4. does the work. Here that's logging `EMAIL SENT for order {OrderId}`.
    5. records the `MessageId` as processed.
12. Finally the handler calls **`CompleteMessageAsync`**. Only now does the broker delete the message.

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

- A bad message doesn't block the others or retry forever. Someone inspects the DLQ (`GET /debug/peek-dlq`), fixes the cause and resubmits.
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

**Services vs instances:** _different services_ (notifications, audit) each keep their own processed list, because each must handle every message. _Instances of the same service_ must share one store, because any instance may receive any message, including a redelivery.

### d) A second subscriber → fan-out

The `audit` subscription was added to `Config.json`. A second NotificationService instance reads from it (§8). Every order reaches both subscriptions, and **OrderService didn't change at all**.
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
├─ EventDemo.slnx                 ← solution file (.NET 10's XML format); groups the 3 projects
├─ global.json                    ← pins the .NET SDK to 10.x
├─ nuget.config                   ← maps every package to nuget.org
├─ .gitignore                     ← standard .NET ignores, plus infra/.env
│
├─ infra\                         ← everything needed to run the broker locally
│  ├─ docker-compose.yaml         ← starts two containers: servicebus-emulator + mssql
│  ├─ Config.json                 ← namespace, topic "orders", subscriptions "notifications" + "audit"
│  └─ .env                        ← SQL password + EULA flag (NOT committed)
│
└─ src\
   ├─ Contracts\
   │  └─ OrderPlacedEvent.cs      ← the event shape both services agree on
   │
   ├─ OrderService\               ← PUBLISHER
   │  ├─ Program.cs               ← client/sender singletons, POST /orders, debug peek endpoints
   │  ├─ DTOs\
   │  │  └─ PlaceOrderRequestDTO.cs  ← the API contract (what a client sends)
   │  ├─ appsettings.json         ← ServiceBus:TopicName = "orders"
   │  └─ OrderService.http        ← requests to send from VS Code (REST Client extension)
   │
   └─ NotificationService\        ← SUBSCRIBER
      ├─ Program.cs               ← client, processor, processed-message store, hosted consumer
      ├─ OrderPlacedConsumer.cs   ← BackgroundService: validate, dedupe, work, complete / dead-letter
      ├─ IProcessedMessageStore.cs ← idempotency store (in-memory, demo only)
      └─ appsettings.json         ← ServiceBus:TopicName + SubscriptionName
```

The **connection string** isn't in any file in the repo. It's in **user secrets**, stored under your user profile and linked to each project by the `UserSecretsId` in its `.csproj`.

---

## 8. How to run it

### Prerequisites

- .NET 10 SDK
- Docker Desktop (on Windows, with the WSL 2 engine)
- VS Code with the **REST Client** extension, to use `OrderService.http` (optional)

### 1. Start the broker

Create `infra/.env`:

```
CONFIG_PATH="./Config.json"
ACCEPT_EULA="Y"
MSSQL_SA_PASSWORD="<strong password: 8+ chars, upper/lower/digit/symbol, no ; = quotes or $>"
EMULATOR_HTTP_PORT=5300
```

`ACCEPT_EULA="Y"` accepts the license terms of the Service Bus emulator and SQL Server. The password ends up inside a SQL connection string, so it must not contain `;`.

Then, from `infra\`:

```bash
docker compose up -d
curl http://localhost:5300/health          # 200 once it's ready (~30 s)
```

Docker reads `.env` and `Config.json` only when it creates the containers. After changing either, run `docker compose up -d --force-recreate`. That also empties the broker, because the containers keep no data.

### 2. Set the connection string (once per project)

```bash
dotnet user-secrets set "ServiceBus:ConnectionString" "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;" --project src/OrderService
dotnet user-secrets set "ServiceBus:ConnectionString" "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;" --project src/NotificationService
```

`SAS_KEY_VALUE` is the emulator's literal, documented key. Don't replace it.

### 3. Start the services, each in its own terminal, from the repo root

```bash
dotnet run --project src/OrderService          # http://localhost:5085
dotnet run --project src/NotificationService   # http://localhost:5291 (subscription: notifications)
```

Stop them with **Ctrl+C**, so the consumer shuts down gracefully.

**Optional extra consumers.** Command-line arguments override `appsettings.json`:

```bash
# fan-out: a consumer on the audit subscription
dotnet run --project src/NotificationService -- --ServiceBus:SubscriptionName=audit --urls http://localhost:5292

# competing consumers: a second instance on the notifications subscription
dotnet run --project src/NotificationService -- --urls http://localhost:5293
```

### 4. Send requests

Open `src/OrderService/OrderService.http` and click **Send Request**, or use curl.

| Request                                      | What it does                                                                                                          |
| -------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| `POST /orders`                               | Places a normal order; returns 202 with the `orderId`                                                                 |
| `POST /orders` with `"customerId": "poison"` | **Test hook:** the handler always throws → 3 immediate retries → DLQ                                                  |
| `POST /orders` with `"customerId": "flaky"`  | **Test hook:** the work runs, but the first delivery isn't completed → redelivery after 30 s → `Duplicate … skipping` |
| `GET /debug/peek`                            | Lists up to 10 messages waiting in `orders/notifications`, **without** consuming them                                 |
| `GET /debug/peek-dlq`                        | Lists up to 10 dead-lettered messages with their reason and delivery count                                            |

The `/debug/*` endpoints and the test hooks exist for learning only. A real service wouldn't ship them, or would enable them only in Development.

### 5. Stop the broker

```bash
docker compose down
```

### Troubleshooting

| Symptom                                                          | Cause                                                                                                     |
| ---------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| `SocketException (10061)` / "target machine actively refused it" | The emulator isn't running or is restarting. Check `docker ps` and `docker logs servicebus-emulator`.     |
| Emulator keeps restarting; log says `Keyword not supported`      | The SQL password contains `;`.                                                                            |
| `NU1100` / "PackageSourceMapping is enabled"                     | A global NuGet config is blocking packages. The repo's `nuget.config` fixes that; make sure it's present. |
| Build error "file is locked by OrderService"                     | A service is still running. Stop it, then build.                                                          |
| `/debug/peek` is empty right after a POST                        | The emulator takes a moment to make a new message readable. Try again after a second or two.              |

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
