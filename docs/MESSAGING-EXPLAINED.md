# How the Messaging library works

A walkthrough of `src/Shared/Messaging`, as built in Milestone 2. It covers what each file does, how one message flows through publishing and consuming, and why the code is shaped the way it is. §11 shows how you'd arrive at this design step by step. Related: [ROADMAP.md](ROADMAP.md), [MILESTONE-2.md](milestones/MILESTONE-2.md), and README §4–§5.

## 1. The 30-second version

The library has **two halves**:

```
PUBLISHING (OrderService)                          CONSUMING (NotificationService)
─────────────────────────                          ───────────────────────────────
your code:  publisher.PublishAsync(evt, ...)        your code:  class OrderPlacedHandler : IEventHandler<OrderPlacedEvent>
                 │                                                     ▲
   Messaging:    ▼                                    Messaging:       │ calls it with a typed event
   MessageEnvelope builds the ServiceBusMessage       SubscriptionProcessor receives, checks,
   ServiceBusEventPublisher sends it                  deserializes, dedupes, completes / dead-letters
                 │                                                     ▲
                 └──────────▶  Service Bus (topic → subscription)  ─────┘
```

The services only state **what** they publish or handle. Messaging takes care of **how**: ids, envelope properties, validation, retries, duplicates and the dead-letter queue.

---

## 2. Why the library exists

Before Milestone 2, each service talked to the Azure SDK directly:
- OrderService built `ServiceBusMessage`s by hand, inside the `POST /orders` endpoint.
- NotificationService had a 116-line `OrderPlacedConsumer`, which mixed broker plumbing (validate, deserialize, dedupe, complete) with business logic (send the email).

In Milestone 4 you add Inventory, Payment and Shipping. Each one both consumes and publishes. Without the library, you'd copy those 116 lines three more times and keep four copies in sync. With it, **each new service writes only small handler classes**.

The rule that keeps it healthy: **Messaging contains plumbing only, never business logic.** That's also why it has **no reference to Contracts**. It doesn't know what an "order" is. It works with any event type you hand it.

---

## 3. The files, in three groups

The folders match these groups. Every file uses the single `Messaging` namespace, so a service needs only `using Messaging;`.

| Folder | File | One-line job |
|---|---|---|
| *(root)* | `ServiceCollectionExtensions.cs` | The entry point: `AddServiceBus`, `AddEventPublisher`, `AddSubscription`. How services register everything |
| `Publishing/` | `MessageEnvelope.cs` | Turns an event into a `ServiceBusMessage` with the standard properties |
| | `IEventPublisher.cs` | What a service depends on to publish: `PublishAsync(...)` |
| | `ServiceBusEventPublisher.cs` | The implementation: envelope, then send |
| `Consuming/` | `IEventHandler.cs` | What a service implements to consume one event type |
| | `MessageContext.cs` | Envelope info handed to a handler, plus the flaky test hook |
| | `SubscriptionBuilder.cs` | What `AddSubscription(...)` returns: the public `.Handle<TEvent, THandler>(subject)` that fills the table |
| | `SubscriptionRegistration.cs` | Internal: the table "subject → how to deserialize, which handler" (`HandlerEntry`) |
| | `SubscriptionProcessor.cs` | The background worker that receives and processes messages |
| `Idempotency/` | `IProcessedMessageStore.cs` | The contract: "was this message already handled?" and "mark it handled" |
| | `InMemoryProcessedMessageStore.cs` | The demo implementation: a dictionary, lost on restart. The M3 database inbox goes next to it |
| `Diagnostics/` | `DebugEndpoints.cs` | `/debug/peek/...` and `/debug/peek-dlq/...` for any topic and subscription |

---

## 4. Publishing, following one `POST /orders`

### At startup (OrderService's Program.cs)

```csharp
builder.Services
    .AddServiceBus(connectionString)        // registers ONE ServiceBusClient (singleton)
    .AddEventPublisher(Topics.Orders);      // registers IEventPublisher for the "orders" topic
```

- **`AddServiceBus`** registers a factory, `_ => new ServiceBusClient(connectionString)`. The client is the long-lived AMQP connection. It's created the first time something needs it, and only once.
- **`AddEventPublisher("orders")`** registers `IEventPublisher` with a factory that:
  1. gets the client
  2. creates a `ServiceBusSender` for `"orders"`
  3. wraps it in `new ServiceBusEventPublisher(sender)`

The **sender is never registered by itself**. Nothing else in OrderService can inject a bare sender and send a message that skips the envelope.

### During the request

```csharp
await eventPublisher.PublishAsync(orderPlacedEvent, EventSubjects.OrderPlaced,
    messageId: orderPlacedEvent.OrderId, correlationId: orderPlacedEvent.OrderId, cancellationToken);
```

1. `ServiceBusEventPublisher.PublishAsync` calls `MessageEnvelope.CreateMessage(...)`.
2. **`MessageEnvelope`:**
   - **refuses** a missing payload, an empty `messageId`, `correlationId` or `subject`. A bad id fails *here*, loudly, instead of quietly breaking duplicate detection later.
   - serializes the event to JSON as the **body**
   - sets the **envelope**: `MessageId`, `CorrelationId`, `Subject`, `ContentType = application/json`, and `SchemaVersion = 1`
3. The publisher calls `SendMessageAsync`. When it returns, the broker has stored the message.

**Who decides what:** the *caller* chooses the ids, because only OrderService knows that an order's identity is its `OrderId`. The *envelope* applies the same rules to every message. That split is why `MessageEnvelope` takes the ids as required parameters and never makes one up.

---

## 5. Consuming: two phases

Consuming is easier to follow in two phases: **wiring at startup**, then **handling each message**.

### Phase A: wiring at startup

NotificationService's Program.cs:

```csharp
builder.Services
    .AddServiceBus(connectionString)
    .AddSubscription(Topics.Orders, "notifications")                         // ①
    .Handle<OrderPlacedEvent, OrderPlacedHandler>(EventSubjects.OrderPlaced); // ②
```

**① `AddSubscription("orders", "notifications")` does four things:**
1. Creates a **`SubscriptionRegistration`**, an object that holds `Topic`, `Subscription` and an empty `Handlers` dictionary.
2. Registers the shared `IProcessedMessageStore`. It uses `TryAdd`, so the store is registered only once, however many subscriptions there are.
3. Registers a **recipe** (a factory) for one `SubscriptionProcessor`: "when the app starts, build a processor for *this* registration". Nothing is built yet.
4. Returns a **`SubscriptionBuilder`** that holds that same registration object. That's what makes `.Handle<…>()` the only thing you can chain next.

**② `.Handle<OrderPlacedEvent, OrderPlacedHandler>("OrderPlaced")` does three things:**
1. Checks the subject isn't empty or already registered on this subscription.
2. Registers `OrderPlacedHandler` in DI as **scoped**, so there's a new instance per message.
3. Adds one row to the registration's `Handlers` table:

```
Handlers["OrderPlaced"] = HandlerEntry(
    Deserialize: body => JsonSerializer.Deserialize<OrderPlacedEvent>(body),
    Invoke:      (sp, evt, ctx, ct) => sp.GetRequiredService<OrderPlacedHandler>()
                                         .HandleAsync((OrderPlacedEvent)evt, ctx, ct))
```

After both calls, memory looks like this:

```
SubscriptionRegistration
  Topic        = "orders"
  Subscription = "notifications"
  Handlers     = { "OrderPlaced" → (Deserialize, Invoke) }
        ▲                 ▲
        │ holds           │ will receive (via the factory)
SubscriptionBuilder   SubscriptionProcessor (not created yet)
```

**When the app starts:** the host asks DI for every `IHostedService`. That runs the factory from ①, which builds the `SubscriptionProcessor` with:
- the registration
- the `ServiceBusClient`
- an `IServiceScopeFactory`
- the store
- a logger

The host then calls `ExecuteAsync`, which attaches the two handlers and calls `StartProcessingAsync`. From then on, the SDK's `ServiceBusProcessor` keeps a connection open and calls `MessageHandler` for each message, one at a time (`MaxConcurrentCalls = 1`).

### Phase B: handling one message (`SubscriptionProcessor.MessageHandler`)

Each message goes through the same steps. Every check that fails **stops** there:

| # | Step | If it fails | Why that outcome |
|---|---|---|---|
| 0 | Log `Received {Subject} {MessageId} … on orders/notifications` | — | With several processors, you can see which one is talking |
| 1 | Find `Handlers[message.Subject]` | **Dead-letter** `UnknownSubject` | Nobody can handle it. A retry would fail the same way |
| 2 | `SchemaVersion == 1`? | **Dead-letter** `InvalidMessage` | A shape we don't understand. Permanent |
| 3 | `entry.Deserialize(body)` | **Dead-letter** `DeserializationFailed` (bad JSON or `null`) | Bad data never fixes itself |
| 4 | Build the `MessageContext` (MessageId, CorrelationId, Subject, DeliveryCount) | — | What the handler is allowed to know |
| 5 | Store has `"notifications:{MessageId}"`? | **Complete** it and skip | Already done. Completing stops it from coming back again |
| 6 | New DI scope → `entry.Invoke(...)` → `OrderPlacedHandler.HandleAsync` | Exception → the SDK **abandons** the message → **retry**, then the DLQ after 3 tries | A throw means "maybe it works later" |
| 7 | Mark `"notifications:{MessageId}"` as processed | — | Done *after* the work: a crash gives a duplicate, never a lost email |
| 8 | Did the handler call `SimulateLostComplete()`? If so, return without completing. Otherwise **complete** | — | Only now does the broker delete the message |

**Permanent vs transient** is the key idea behind this table:
- Steps 1–3 fail the *same way every time*, so they go straight to the DLQ.
- Step 6 might be a temporary problem (a database down, a timeout), so it's retried.

---

## 6. The "clever bits", slowly

These are the parts most likely to feel like magic.

### a) Why `IEventHandler<TEvent>` has the generic on the interface

- `IEventHandler<OrderPlacedEvent>` means: *"this class handles **one** event type, and gets it already typed."* So `OrderPlacedHandler.HandleAsync` receives an `OrderPlacedEvent`, not an `object`.
- `IEventPublisher.PublishAsync<TEvent>` is the opposite: **one** publisher sends **any** type, so its generic is on the *method*.
- Where the generic goes says which of the two you mean.

### b) The two lambdas: how it calls a typed handler without reflection

The processor only knows `object`s: it reads a string body and doesn't know what `OrderPlacedEvent` is. The trick is that **`Handle<OrderPlacedEvent, OrderPlacedHandler>()` knows both types at the moment you call it**, so it bakes them into two small functions:
- `Deserialize` already "knows" to produce an `OrderPlacedEvent`.
- `Invoke` already "knows" to resolve an `OrderPlacedHandler` and cast the `object` back to `OrderPlacedEvent`.

Later, the processor just looks up the subject and calls whatever functions it finds. Each new event type adds one row to the table, and the processor's code never changes.

The constraint `where THandler : IEventHandler<TEvent>` adds compile-time safety: `Handle<OrderPlacedEvent, PaymentHandler>()` simply won't compile.

### c) Why `.Handle()` *after* `AddSubscription` still works

`AddSubscription` doesn't build the processor; it registers a recipe. The recipe captures a **reference** to the registration object, not a copy. By the time the app starts and the recipe runs, `.Handle()` has already added its row to *that same object*. If the registration were copied, the processor would get an empty table.

### d) Why not `AddHostedService<SubscriptionProcessor>()`

`AddHostedService` ignores a second registration of the **same type**. In Milestone 4, NotificationService calls `AddSubscription` four times. With `AddHostedService`, only the first subscription would ever be consumed, **with no error at all**. `AddSingleton<IHostedService>(factory)` registers each one separately.

That's also why `AddSubscription` doesn't put the registration into DI by its type. With four of them, asking DI for "the" registration would return only the last one.

### e) Why one DI scope per message

Each message is its own unit of work. In Milestone 3 the handler will use a `DbContext`. That must be fresh for each message, and never shared between them. `CreateAsyncScope()` creates a fresh set of scoped services, and `await using` disposes them when the message is done. It's async because a DbContext needs to be disposed asynchronously. Development mode also checks that scoped services are never taken from the root container, so this scope is required, not optional.

### f) Who can see what (`public` vs `internal`)

| Visible to services (`public`) | Hidden inside Messaging (`internal`) |
|---|---|
| `IEventPublisher`, `IEventHandler<T>`, `MessageContext`, `SubscriptionBuilder`, the `Add…` and `Map…` methods | `SubscriptionRegistration`, `HandlerEntry`, `SubscriptionProcessor`, `MessageContext.CompleteSuppressed` |

Services can only use the left column. That means you can rewrite anything on the right in Milestone 3 without touching a single service.

---

## 7. The test hooks, through the new design

- **poison:** `OrderPlacedHandler` throws. The exception goes through `entry.Invoke` (step 6) to the SDK, which abandons the message. It's redelivered at once, three times, then moved to the DLQ with `MaxDeliveryCountExceeded`. **The library didn't need any special code for this.**
- **flaky:** on delivery 1, the handler does the work and calls `context.SimulateLostComplete()`. The processor marks the message processed (step 7), then sees the flag (step 8) and **doesn't** complete it. The 30-second lock expires, and the broker delivers it again. On delivery 2, step 5 finds it in the store, logs `Duplicate … skipping` and completes it.

The flag is `internal` with a `private set`, so a handler can **ask** to skip completion but can't read or undo it. Only the library decides.

---

## 8. Why this pays off in the next milestones

- **Milestone 3 (database, outbox and inbox):**
  - `IProcessedMessageStore` is replaced by an inbox table. Because of `TryAdd`, a service just registers its own store first.
  - The per-message scope is where the DbContext lives.
  - `IEventPublisher` starts writing to an outbox table instead of sending directly.
- **Milestone 4 (Inventory, Payment and Shipping):**
  - Each service is a few lines of `AddSubscription(...).Handle<…>()` plus small handler classes.
  - OrderService's `inventory` subscription gets two subjects, with two `.Handle()` calls on one subscription.
  - NotificationService gets four `AddSubscription` calls.
- **Milestone 5 (tracing):** it hooks in centrally: in the envelope, the publisher and `ServiceDefaults`.

---

## 9. Interview cheat sheet

- *"Services depend on `IEventPublisher` and implement `IEventHandler<T>`. A shared library handles the broker: envelope, validation, idempotency, settlement."*
- *"Permanent failures (unknown subject, wrong schema version, bad JSON) are dead-lettered immediately. Handler exceptions are treated as transient: the message is abandoned and retried up to MaxDeliveryCount."*
- *"The consumer is idempotent: it checks a processed store before the work and records the message after it, so a crash gives a duplicate, never a loss. Duplicates are still completed."*
- *"Handlers are dispatched by Subject, with deserialization and invocation captured in delegates at registration time, so there's no reflection and it's checked at compile time."*
- *"One DI scope per message, because each message is its own unit of work."*
- *"The library holds plumbing only, with no business types, because every service depends on its version."*
- *"MessageId is deterministic and chosen by the producer. CorrelationId is the OrderId, so one order can be followed across services. Tracing uses `Diagnostic-Id` separately."*

---

## 10. See it for yourself

The best way to make this stick is to step through one message in the debugger:
1. Run NotificationService with `dotnet run` (README §8, option B).
2. Put a breakpoint at the top of `SubscriptionProcessor.MessageHandler`.
3. POST an order, and step through steps 1–8. Watch `entry` and `evt` change type at step 3.

---

## 11. How you'd arrive at this design

The code wasn't designed in one go. You get to it by starting with the simplest version that works, hitting a problem, and fixing only that problem. Each step below ends at a piece of the code you already have.

### Step 0: "I just need to receive one message"

> *"OrderService publishes `OrderPlaced`. NotificationService needs to receive it and log an email. What's the least code that does that?"*

That's the original `OrderPlacedConsumer.cs` (it's still in git history):

```csharp
class OrderPlacedConsumer : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var processor = client.CreateProcessor("orders", "notifications");
        processor.ProcessMessageAsync += async args =>
        {
            var evt = JsonSerializer.Deserialize<OrderPlacedEvent>(args.Message.Body);
            logger.LogInformation("EMAIL SENT for {OrderId}", evt.OrderId);
            await args.CompleteMessageAsync(args.Message);
        };
        await processor.StartProcessingAsync(ct);
        await Task.Delay(Timeout.Infinite, ct);
    }
}
```

**This is the right place to start.** Everything after it is a reaction to a problem with it.

### Step 1: "I'll need this in 4 more services. Copy-paste?"

> *"Inventory, Payment, Shipping and Order all need consumers. Copying this class 10 times would be terrible."*

The question to ask is **"what's different each time, and what's the same?"**

| Different per consumer | Same every time |
|---|---|
| topic + subscription name | create processor, start, stop |
| event type (`OrderPlacedEvent`, `StockReserved`, …) | check subject and schema, deserialize |
| the business logic (send email, reserve stock…) | dead-letter bad messages, complete good ones |
| | duplicate check, logging |

**The left column belongs to the service, and the right column goes into a library.** The business logic needs a shape the library can call, hence `IEventHandler<TEvent>`.

→ `Consuming/IEventHandler.cs`, plus `NotificationService/Handlers/OrderPlacedHandler.cs`, which holds only business code.

### Step 2: "Write the code I *wish* I could write"

> *"Before building the library, what do I want Program.cs to look like?"*

**Design the calling code first**:

```csharp
services.AddSubscription("orders", "notifications")
        .Handle<OrderPlacedEvent, OrderPlacedHandler>("OrderPlaced");
```

Then work backward from it:
- `.Handle` is chained, so `AddSubscription` must **return an object** with `Handle`. That's the `SubscriptionBuilder`.
- `Handle` must remember "subject → handler" **somewhere the processor can read later**. That's the shared `SubscriptionRegistration`.
- One subscription can receive **several** subjects. In M4, `payments/order` receives both `PaymentSucceeded` and `PaymentFailed`. So `.Handle` must be chainable more than once, and the registration holds a **dictionary**.

→ `Consuming/SubscriptionBuilder.cs` (the `.Handle` you call) and `Consuming/SubscriptionRegistration.cs` (what it fills: `Handlers`, a `Dictionary<string, HandlerEntry>`).

### Step 3: "The library doesn't know what `OrderPlacedEvent` is" (the hard step)

> *"The processor must deserialize into `OrderPlacedEvent` and call `OrderPlacedHandler`, but Messaging doesn't reference Contracts. How do I call a type I don't know?"*

There are three attempts:

- **A: a generic processor, `SubscriptionProcessor<TEvent>`.** ✗ One subscription delivers *several* event types (Step 2), and a class has only one `TEvent`.
- **B: store `Type` objects and use reflection** (`JsonSerializer.Deserialize(body, eventType)`, `GetMethod("HandleAsync").Invoke(...)`). ✓ It works. ✗ But it's ugly, typos fail only at runtime, and it's hard to debug.
- **C, the aha: "where *are* the types known?"** The answer is inside `Handle<TEvent, THandler>`. So do the typed work **there, wrapped in lambdas**, and give the processor functions that take and return plain `object`:

```csharp
new HandlerEntry(
    body => JsonSerializer.Deserialize<TEvent>(body),                         // TEvent is known HERE
    (sp, evt, ctx, ct) => sp.GetRequiredService<THandler>().HandleAsync((TEvent)evt, ctx, ct));
```

The processor calls `entry.Deserialize(...)` and `entry.Invoke(...)` without knowing the types. **The lambdas remember them.** That's why, at runtime, the trace jumps from the processor back into `SubscriptionBuilder.cs`. Those lambdas were written at registration time, and they run when a message arrives.

### Step 4: "How does the processor get *its* registration?"

> *"I'll do `services.AddHostedService<SubscriptionProcessor>()` and `services.AddSingleton(registration)`."*

This fails **quietly**:
- With two subscriptions, DI hands out the **last** registration, so both processors read the same subscription.
- `AddHostedService<T>` **ignores** a second registration of the same type, so only one processor runs.

The next thought is: *"each processor needs **its own** registration, and `AddSubscription` already has it in a local variable."* So you register a **factory lambda that captures it**:

```csharp
services.AddSingleton<IHostedService>(sp => new SubscriptionProcessor(registration, ...));
```

→ `ServiceCollectionExtensions.AddSubscription` (the comment above that line records this lesson). See also §6d.

### Step 5: "How long does each thing live?"

> *"In M3 my handler needs a `DbContext`, which is scoped. But the processor is a singleton that lives forever…"*

A singleton that holds a scoped object keeps that one DbContext forever, and every message shares it (the "captive dependency" bug). For every object, ask **"how long should it live?"**

| Object | Lifetime | Why |
|---|---|---|
| `ServiceBusClient` | singleton | one connection per app, expensive to create |
| `SubscriptionProcessor` | singleton (hosted) | runs for the app's whole life |
| `IProcessedMessageStore` | singleton | must remember across messages |
| `OrderPlacedHandler` (+ DbContext later) | **per message** | fresh state for each message |

"Per message" means **one DI scope per message**: `CreateAsyncScope()` in step 6 of `MessageHandler`. It's also why `Handle` registers the handler with `AddScoped`. See also §6e.

### Step 6: "What can go wrong with a message?"

> *"So far I've written the happy path. For each way a message can be bad, I'll ask: **would retrying fix it?**"*

| What's wrong | Will retry fix it? | So… |
|---|---|---|
| Subject nobody handles | No, never | dead-letter **now** (`UnknownSubject`) |
| Wrong SchemaVersion | No | dead-letter now (`InvalidMessage`) |
| JSON doesn't parse | No | dead-letter now (`DeserializationFailed`) |
| Handler throws (DB down, bug, out-of-order event) | **Maybe** | let it throw, and the SDK abandons and retries; after MaxDeliveryCount the broker dead-letters it |

Two rules come from this:
1. **Do the cheap, certain checks first** (subject, schema, JSON), before any real work. That's why steps 1–3 come before the handler.
2. **Don't catch handler exceptions.** Wrapping everything in `try/catch` and logging the error *completes a failed message*, and the order is lost. The SDK's retry-then-DLQ behaviour is exactly what you want.

### Step 7: "At least once means it can arrive twice"

> *"If my handler finishes but the Complete call is lost, the lock expires and I get the message again. Two emails!"*

You need a duplicate check, and the important part is **where it goes**:

```
check "seen it?" → run handler → mark "seen" → complete
```

- **Mark before the handler?** ✗ If the handler crashes, the retry is skipped as a "duplicate", and the work never happens.
- **Mark after Complete?** ✗ If Complete is the call that got lost, you never mark it, so the retry runs the handler again.
- **Mark between handler and Complete?** ✓ That's the only safe spot.

**The key includes the subscription** (`"notifications:{messageId}"`) because in fan-out **the same MessageId arrives on every subscription**. The M3 inbox table uses the same composite key.

Then you ask *"how do I **test** this? I can't make the network fail on purpose."* That's where `SimulateLostComplete()` comes from: it sets a flag, and the processor skips Complete. You trigger it with the `flaky` order (§7).

### Step 8: "What happens when the app stops?"

> *"If I kill it mid-message, is the message lost?"*

No: an incomplete message's lock expires, and it's redelivered. But it's cleaner to stop pulling, finish the current message, and close the link. That's `StopAsync`.

### The habits behind it

Each step is one of these questions, asked at the right time:

1. **Make the simplest version work first** (Step 0). Don't design a library before you have one working consumer.
2. **Generalize on the second copy, not the first** (Step 1). Split *what varies* from *what's the same*.
3. **Write the calling code you wish existed, then build backward** (Step 2).
4. **"Where is this type known?"** Do the typed work there and pass out a lambda (Step 3).
5. **"Is this one per app, or one per thing?"** It settles both the DI registration (Step 4) and the lifetimes (Step 5).
6. **For every failure: "will retrying fix it?"** (Step 6)
7. **For every side effect: "what if this runs twice?"** and "what if the next step never happens?" (Step 7)
8. **"How will I prove it works?"** If you can't trigger it, add a test hook (Step 7).

**Exercise:** on paper, start from Step 0's code and try to reach the current `SubscriptionProcessor` by asking only those 8 questions. If you can, you understand the code better than if you'd memorized it.
