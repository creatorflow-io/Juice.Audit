# Quickstart: Audit Collector Pipeline

How to check the feature end to end once it's built. The paths are in this repo.

## 1. Remote mode (app → broker / gRPC → collector → db)

**Collector**, `test/Juice.Audit.Tests.GrpcHost/Program.cs`:

```csharp
builder.Services.ConfigureAuditGrpcHost(builder.Configuration);
builder.Services.AddAuditCollector();
builder.Services.AddMessaging(m => m
    .AddIdempotencyInMemory()
    .AddRabbitMQ(r => r.AddConsumer("audit", "juice.audit", "rabbitmq", c => c.SubscribeAuditDataChanges())));
app.MapAuditGrpcServer();
app.MapJuiceAudit("/api/audit");
```

**App**, `test/Juice.Audit.Tests.Host/Program.cs`. The host has no business context today, so this feature adds a
sample one: `SampleOrderContext`, a `DbContextBase` with an auditable `Order`, an `IOutboxContext`, and a
`TransactionBehavior` subclass. Then switch to the gRPC client and add the outbox for that context:

```csharp
builder.Services.ConfigureAuditGrpcClient(o => o.Address = new Uri("https://localhost:7285"));
builder.Services.AddAuditOutbox<SampleOrderContext>(o => o.AppName = "audit-test-host");
// appsettings: Audit:Export, and a publishing policy routing "juice.audit" to "rabbitmq"
```

**Check**:
1. Send a `POST` that changes an audited entity. The response returns at once.
2. Look up `GET /api/audit/traces/{X-Trace-Id}` on the collector. Within about 2 s it shows the access record, and
   the data changes once the outbox has delivered them.
3. Stop the collector and send 100 requests. They still succeed, and `juice.audit.export.queue.size` rises. Start
   the collector again: the queue drains and every record appears once.
4. Stop RabbitMQ and change data. The outbox rows stay `NotPublished`. Start RabbitMQ: the changes arrive.

## 2. Crash test (SC-001)

`test/Juice.Audit.Scopes.Tests/OutboxCrashTests.cs` (to be written; it needs a database, so it uses `IgnoreOnCIFact`).
Commit a change, then throw away the service provider before `TransactionCommitted` is handled and before any
export. Build a new provider with the delivery processor and the local consumer. The `DataAudit` row must appear.
For the rollback case, it must not.

## 3. In-process mode (single app, no broker)

Use the in-process registration from [contracts/public-api.md](contracts/public-api.md#in-process-mode-one-app-no-broker).
Same checks as section 1, steps 1 and 2.

## 4. Unit tests (no infrastructure)

```powershell
dotnet test test/Juice.Audit.Scopes.Tests -f net10.0
```

- `ExportQueueTests`: batching by size and age, drop when full, retry and backoff, flush on shutdown, synchronous
  mode.
- `OutboxSinkTests`: one event per transaction, appends across rounds, a split at the limit, each fallback reason
  (research R4), the access record's `OutboxedChanges` count.
- `CollectorTests`: duplicate ids skipped, processors in order, a processor that throws fails only its record.
