# EventDemo — two services talking through Azure Service Bus

> A practice project for interview prep. It's small on purpose: the aim is to *understand* event-driven messaging well enough to explain it, not to build a real shop.

---

## 1. The one-sentence version

**OrderService** takes an order over HTTP and announces *"an order was placed"*. **NotificationService** hears that announcement and reacts, for example by "sending" a confirmation email. The two services never call each other. They only talk through **Azure Service Bus**, which runs locally in Docker.

---

## 2. Why build it this way? (The problem messaging solves)

The obvious way to connect two services is a direct HTTP call:

```
Client ──POST /orders──▶ OrderService ──POST /send-email──▶ NotificationService
```

It works until something goes wrong:

| Problem | What happens with a direct HTTP call |
|---|---|
| NotificationService is down or redeploying | OrderService's call fails, and the order either fails too or the email is lost. |
| NotificationService is slow | The customer waits for an email to be sent before seeing "order placed". |
| A second service needs to know about orders (audit, analytics, shipping) | You change OrderService to call it too. OrderService ends up knowing about everyone. |
| A traffic spike | NotificationService gets hit at full speed and may fall over. |

Putting a **message broker** in the middle fixes each of these:

```
Client ──POST /orders──▶ OrderService ──publish──▶ [ Service Bus ] ──deliver──▶ NotificationService
                                                         │
                                                         └──deliver──▶ (any future subscriber)
```

- **Temporal decoupling:** the broker *stores* the message. If NotificationService is down, the message waits and gets processed when it comes back.
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
│  │                      └── subscription: audit   (added later)       │
│  │                                                                     │
│  │  mssql  ← where the emulator stores messages and their state       │
│  └─────────────────────────────────────────────────────────────────────┘
└────────────────────────────────────────────────────────────────────────┘
```

| Piece | What it is | Its job |
|---|---|---|
| **Service Bus emulator** | A Docker container from Microsoft that behaves like Azure Service Bus | The broker: accepts, stores and delivers messages |
| **mssql** | SQL Server in Docker | The emulator's storage. It's why messages survive a restart |
| **Namespace** (`sbemulatorns`) | The top-level container, like a "server" | Holds topics and queues. In Azure, this is the resource you pay for |
| **Topic** (`orders`) | A named channel publishers send to | Receives each `OrderPlacedEvent` |
| **Subscription** (`notifications`) | A named "inbox" attached to a topic | Gets **its own copy** of every message sent to the topic |
| **Contracts** | A .NET class library | Defines the shape of the message (`OrderPlacedEvent`) that both services agree on |
| **OrderService** | ASP.NET Core minimal API | The **publisher (producer)** |
| **NotificationService** | ASP.NET Core app with a background worker | The **subscriber (consumer)** |

### Topic vs queue

- **Queue:** one message goes to exactly *one* receiver. Use it for **commands** ("send this email"), where one handler does the work.
- **Topic + subscriptions:** one message is copied to *every* subscription. Use it for **events** ("an order was placed"), where any number of services may care.

We use a topic because `OrderPlaced` is an event. Inside a single subscription, though, each message is still handled once. If you ran three copies of NotificationService, they would *share* the `notifications` subscription and split the work between them (the **competing consumers** pattern).

---

## 4. The life of one order, step by step

### Publishing (OrderService, milestone 3)

1. A client sends `POST /orders` with a JSON body: customer, email, amount and currency. This is the `PlaceOrderRequest` (the **API contract**).
2. OrderService generates the parts the client shouldn't control, such as the `OrderId` and the time the order was placed.
3. It builds an `OrderPlacedEvent` (the **event contract**, from the Contracts library).
4. It wraps the event in a `ServiceBusMessage`:
   - **Body:** the event serialized to JSON.
   - **Envelope properties:** metadata *about* the message, which the broker and consumers can read without opening the body.
     - `MessageId`: a unique id, used for duplicate detection.
     - `Subject`: `"OrderPlaced"`, the type of the event.
     - `ContentType`: `"application/json"`.
     - `CorrelationId`: ties this message to the HTTP request that caused it, for tracing.
     - `ApplicationProperties["SchemaVersion"]`: which version of the event shape this is.
5. It calls `SendMessageAsync`. When that returns, **the broker has stored the message durably**. That's the guarantee OrderService relies on.
6. OrderService returns **202 Accepted**: "I've accepted your order and the rest will happen". It doesn't return 201 Created, because downstream work hasn't happened yet.

### In the broker

7. The topic `orders` gets the message and puts a copy in each subscription. Right now there's only `notifications`.
8. The copy sits in the subscription in the **Active** state, waiting. It stays there whether or not NotificationService is running, until it's consumed or expires (time to live is 1 hour in `Config.json`).

### Consuming (NotificationService, milestone 4)

9. NotificationService runs a `ServiceBusProcessor` inside a `BackgroundService`. It keeps an open connection and asks for messages from the `orders/notifications` subscription.
10. The broker hands over a message in **peek-lock** mode. The message isn't deleted. It's **locked** to this consumer for 30 seconds (`LockDuration` in `Config.json`), so no other consumer can take it.
11. Your handler deserializes the body into an `OrderPlacedEvent` and does the work. Here, that's logging "Sending confirmation to {Email} for order {OrderId}".
12. When the work succeeds, the handler calls **`CompleteMessageAsync`**. Only now does the broker delete the message.

```
Active ──(receive)──▶ Locked ──(complete)──▶ deleted ✅
                        │
                        ├──(handler throws / abandon)──▶ Active again, DeliveryCount + 1
                        ├──(lock expires, e.g. crash)───▶ Active again, DeliveryCount + 1
                        └──(DeliveryCount reaches MaxDeliveryCount = 3)──▶ Dead-letter queue ☠️
```

---

## 5. Why the code is shaped the way it is

| Decision | Reason |
|---|---|
| `ServiceBusClient` and `ServiceBusSender` are **singletons** | The client holds one long-lived AMQP connection, and opening it is expensive. Creating one per HTTP request would waste it and can exhaust connections. |
| A `PlaceOrderRequest` **separate** from `OrderPlacedEvent` | The public API and the event change for different reasons and are used by different people. Keeping them apart means one can change without breaking the other. |
| Envelope data in message properties, **not in the body** | Brokers and consumers can filter, route and trace on properties without deserializing the body. |
| `required` properties on the event | A missing field fails loudly instead of quietly becoming `""` or "now". |
| The publisher doesn't know any subscription names | That's the point of a topic: the producer is decoupled from its consumers. |
| `AutoCompleteMessages = false` in the consumer | You decide when a message counts as handled: only after the work actually succeeded. |
| Connection string in **user secrets** | The emulator key isn't really secret, but a real Azure key is. Build the habit now. |
| `nuget.config` in the repo | Overrides a global NuGet setting that was blocking packages, and documents where packages come from. |

---

## 6. What happens when things go wrong (milestone 5)

Most of the learning is here. Each scenario is an experiment you'll run.

### a) The consumer is offline
Stop NotificationService, post 5 orders, then start it again. All 5 get processed.
**Lesson:** the broker stores messages, so producer and consumer don't need to be up at the same time (**temporal decoupling**).

### b) The handler keeps failing (a "poison message")
Make the handler throw for one specific order. The message goes back to Active each time, its `DeliveryCount` rises, and after 3 attempts it moves to the **dead-letter queue (DLQ)**. The DLQ is a side queue at `orders/subscriptions/notifications/$deadletterqueue`.
**Lesson:** a bad message doesn't block the others or retry forever. Someone (or some tool) inspects the DLQ, fixes the problem and resubmits.

### c) The consumer crashes after doing the work but before completing
The email was "sent", but the broker never got the `Complete`. The lock expires, the message is delivered again, and the customer gets **two emails**.
**Lesson:** Service Bus gives **at-least-once** delivery, not exactly-once. Consumers must be **idempotent**: handling the same message twice must cause no extra harm. A common technique is to remember processed `MessageId`s and skip repeats.

### d) A second subscriber (fan-out)
Add an `audit` subscription to `Config.json` and restart the emulator. Every order now reaches both subscriptions, and **OrderService didn't change at all**.
**Lesson:** this is the loose coupling a topic gives you.

### e) The dual-write problem (discussion, not built)
In a real OrderService you'd *save the order to a database* and *publish the event*. Those are two separate systems and there's no shared transaction between them. If the save succeeds and the publish fails, the order exists but nobody hears about it.
**The fix is the transactional outbox:** in the *same* database transaction, save the order **and** an "outbox" row containing the event. A separate background job reads the outbox and publishes, retrying until it succeeds. Combined with idempotent consumers, nothing is lost and duplicates do no harm.

---

## 7. Repository map

```
F:\Interview\
├─ README.md                  ← this file
├─ EventDemo.slnx             ← solution file (.NET 10's new XML format); groups the 3 projects
├─ global.json                ← pins the .NET SDK to 10.x (you also have the 11 preview installed)
├─ nuget.config               ← "get every package from nuget.org"; overrides your global mapping
├─ .gitignore                 ← standard .NET ignores, plus infra/.env
│
├─ infra\                     ← everything needed to run the broker locally
│  ├─ docker-compose.yaml     ← starts two containers: servicebus-emulator + mssql
│  ├─ Config.json             ← declares namespace, topic "orders", subscription "notifications"
│  └─ .env                    ← SQL password + EULA flag (NOT committed)
│
└─ src\
   ├─ Contracts\
   │  └─ OrderPlacedEvent.cs  ← the event shape both services agree on
   │
   ├─ OrderService\           ← PUBLISHER
   │  ├─ Program.cs           ← (milestone 3) register client/sender, map POST /orders
   │  ├─ appsettings.json     ← ServiceBus:TopicName = "orders"
   │  └─ OrderService.http    ← requests you can send from VS Code (REST Client extension)
   │
   └─ NotificationService\    ← SUBSCRIBER
      ├─ Program.cs           ← (milestone 4) register client + a background worker
      └─ appsettings.json     ← ServiceBus:TopicName + SubscriptionName
```

The **connection string** isn't in any file in the repo. It's in **user secrets**, stored under your Windows profile and linked to each project by the `UserSecretsId` in its `.csproj`. You can see them with `dotnet user-secrets list --project src/OrderService`.

---

## 8. How to run it

```bash
# 1. Start the broker (from infra\)
docker compose up -d
curl http://localhost:5300/health          # should return 200

# 2. Start each service in its own terminal (from the repo root)
dotnet run --project src/OrderService       # http://localhost:5085
dotnet run --project src/NotificationService  # http://localhost:5291

# 3. Place an order (or use OrderService.http in VS Code)
curl -X POST http://localhost:5085/orders -H "Content-Type: application/json" -d "{...}"

# 4. Stop the broker when done
docker compose down
```

---

## 9. Progress

- [x] **Milestone 1: environment.** Emulator and SQL running in Docker, health check passing.
- [x] **Milestone 2: skeleton.** Solution, 3 projects, references, packages, config, secrets, event record, first commit.
- [ ] **Milestone 3: publisher.** `POST /orders` publishes `OrderPlacedEvent`; `GET /debug/peek` shows it without consuming it.
- [ ] **Milestone 4: consumer.** NotificationService processes each order once and completes it.
- [ ] **Milestone 5: break it.** Offline consumer, poison message → DLQ, idempotency, fan-out to `audit`.
- [ ] **Milestone 6 (optional): real Azure.** Standard namespace, swap the connection string, try `DefaultAzureCredential`.

---

## 10. Glossary

| Term | Meaning |
|---|---|
| **Broker** | The middleman that stores and delivers messages (Service Bus here) |
| **Producer / publisher** | Sends messages (OrderService) |
| **Consumer / subscriber** | Receives and handles messages (NotificationService) |
| **Event** | A fact about the past ("OrderPlaced"). Owned by the producer, 0..n consumers, sent to a topic |
| **Command** | A request to do something ("SendEmail"). Owned by the receiver, exactly 1 handler, sent to a queue |
| **AMQP 1.0** | The open wire protocol Service Bus uses (port 5672); a long-lived binary connection |
| **Peek** | Look at messages without locking or removing them |
| **Peek-lock** | Receive a message under a temporary lock; it's deleted only when you *complete* it |
| **Receive-and-delete** | The message is deleted as soon as it's handed out. Faster, but lost if you crash |
| **Complete / Abandon** | Tell the broker "done, delete it" / "I couldn't handle it, give it back" |
| **Lock duration** | How long a received message stays invisible to others (30 seconds here) |
| **Delivery count** | How many times the message has been handed out |
| **Dead-letter queue (DLQ)** | Where messages go after too many failed attempts, or when they expire |
| **At-least-once** | Every message is delivered one or more times, so duplicates are possible |
| **Idempotent** | Processing the same message twice has the same effect as processing it once |
| **Fan-out** | One published message reaching many subscribers |
| **Competing consumers** | Several instances sharing one subscription and splitting the work |
| **Eventual consistency** | The other services catch up shortly after, not instantly |
| **Transactional outbox** | Save the data and the outgoing event in one DB transaction; publish separately |
| **Tolerant reader** | A consumer that ignores fields it doesn't know, so additive changes are safe |

---

## 11. The 60-second interview version

> "I built two small ASP.NET Core services that communicate through Azure Service Bus, running against the local emulator in Docker. OrderService exposes a POST endpoint, publishes an `OrderPlaced` event to a topic and returns 202, because the downstream work happens asynchronously. NotificationService uses a `ServiceBusProcessor` in a background service, receives in peek-lock mode and only completes the message after the work succeeds.
>
> Then I broke it on purpose. With the consumer offline, messages waited in the subscription. When the handler threw, the delivery count went up until the message was dead-lettered. I showed that a crash before completing causes redelivery, which is why the handler is idempotent on `MessageId`. And I added a second subscription to show fan-out without touching the publisher.
>
> The thing I'd add in production is a transactional outbox, because saving the order and publishing the event are two separate systems with no shared transaction."
