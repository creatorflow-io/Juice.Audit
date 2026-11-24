# Contract: Public API (registration, options, extension points)

All of it is additive. The existing method signatures are unchanged (FR-023).

## App: access records (`Juice.Audit`, `Juice.Audit.Api`)

```csharp
// Existing; same signatures, new behavior: records are queued and exported in the background.
services.ConfigureAuditGrpcClient(o => o.Address = new Uri("https://audit"));      // remote mode
services.ConfigureAuditDefault(configuration);                                     // in-process mode

// NEW: exporter options, bound from "Audit:Export" by both calls above.
services.Configure<AuditExportOptions>(o =>
{
    o.QueueCapacity = 10_000;
    o.MaxBatchSize = 512;
    o.MaxBatchAge = TimeSpan.FromSeconds(2);
    o.ExportTimeout = TimeSpan.FromSeconds(10);
    o.MaxRetryAttempts = 5;
    o.ShutdownFlushTimeout = TimeSpan.FromSeconds(5);
    o.Synchronous = false;            // true: export inside the scope (tests)
});
```

```csharp
namespace Juice.Audit;

public sealed class AuditExportOptions { /* the properties above */ }

/// Sends batches to a collector. Implemented: gRPC (remote), in-process (mediator → EF).
public interface IAuditExporter
{
    Task<AuditExportResult> ExportAsync(IReadOnlyList<AuditExportItem> batch, CancellationToken cancellationToken);
}

/// Where AuditContext hands a data change taken by the outbox. Registered by Juice.Audit.Messaging.
public interface IDataChangeSink
{
    /// true: the change goes with the transaction's outbox; false: carry it with the access record.
    /// Never throws.
    bool TryAppend(Guid transactionId, DataAudit entry);
}
```

`IAuditService` is unchanged. Its registered implementation becomes `QueuedAuditService`, which enqueues and returns
`OperationResult.Success`, also when the queue is full: the drop is counted (`dropped{reason=queue_full}`) and
logged at a limited rate, so `AuditRecorder` doesn't warn once per record. In `Synchronous` mode it returns the
exporter's result.

## App: data changes (`Juice.Audit.Messaging`)

```csharp
services.AddMessaging(m => m.AddOutbox() /* , broker or AddLocalPublisher() */);

// NEW: one call per audited context that runs under TransactionBehavior.
services.AddAuditOutbox<OrderingContext>(o =>
{
    o.AppName = "ordering-api";
    o.MaxEntriesPerEvent = 500;
    o.MaxEventBytes = 512 * 1024;
    o.RequireScope = false;           // research R5
});
```

Publishing policy: `DataChangesAudited` is in domain `juice.audit`. The app's policy must route it, e.g.
`"juice.audit": [{ "PublisherKey": "rabbitmq", "Destination": "juice.audit" }]`. Without a route, the changes fall
back to best effort, and a warning is logged once per tenant (research R4.3).

## Collector (`Juice.Audit.Api`, `Juice.Audit.Messaging`)

```csharp
// Existing
services.ConfigureAuditGrpcHost(configuration);
app.MapAuditGrpcServer();             // now also serves LogBatch
app.MapJuiceAudit("/api/audit");      // unchanged

// NEW: processors and options, bound from "Audit:Collector"
services.AddAuditCollector(o =>
{
    o.RedactHeaders = ["Authorization", "Cookie", "Set-Cookie", "X-Api-Key"];   // default list
    o.RedactMask = "***";
    o.MaxBodyLength = 64 * 1024;      // Req_Data, Res_Data, Res_Err, DataAudit.Changes
})
.AddProcessor<MyEnrichmentProcessor>();

// NEW: data change consumer (any Juice consumer builder: RabbitMQ, NATS, local)
messaging.AddRabbitMQ(r => r.AddConsumer("audit", "juice.audit", "rabbitmq",
    c => c.SubscribeAuditDataChanges()));
```

```csharp
namespace Juice.Audit.Collector;

public interface IAuditRecordProcessor
{
    int Order { get; }                                   // ascending
    ValueTask<ProcessorResult> ProcessAsync(AuditRecordEnvelope record, CancellationToken cancellationToken);
}
```

Built-in processors: `HeaderRedactionProcessor` (Order 100) and `TruncationProcessor` (Order 200).

## Choosing the mode (FR-022)

One call, inside the app's messaging setup. `Audit:Mode` picks the mode; switching needs no code change.

```csharp
services.AddMessaging(m => m
    .AddOutbox()
    .AddRabbitMQ(...)                                    // the app's own broker, used in Remote mode
    .AddAuditPipeline<OrderingContext>(configuration, o =>   // Audit:Mode = Remote | InProcess (required)
    {
        o.ConfigureDb = db => db.DatabaseProvider = "PostgreSQL";             // InProcess only, optional
        o.ConfigureGrpcClient = g => g.ChannelOptionsActions.Add(c => { });   // Remote only, optional
        o.ConfigureOutbox = ob => ob.AppName = "ordering-api";                // both, optional
    }));
```

Configuration keys:

| Key | Mode | Notes |
|---|---|---|
| `Audit:Mode` | both | Required: `Remote` or `InProcess`. A missing or unknown value fails at startup. |
| `Audit:Grpc:Address` | Remote | The collector's URL. Required unless `ConfigureGrpcClient` sets `Address`. |
| `Audit:Export:*` | both | Exporter options (above). |
| `Audit:Outbox:*` | both | `AuditOutboxOptions`, applied before `ConfigureOutbox`. |

| `Audit:Mode` | What `AddAuditPipeline` registers |
|---|---|
| `InProcess` | `ConfigureAuditDefault`, `AddLocalPublisher`, `AddLocalConsumer(c => c.SubscribeAuditDataChanges())`, `AddDeliveryProcessor<TContext>("local")`, `AddAuditOutbox<TContext>()` |
| `Remote` | `ConfigureAuditGrpcClient` (address from `Audit:Grpc:Address` or `ConfigureGrpcClient`), `AddAuditOutbox<TContext>()` |

The app still routes `juice.audit` in its publishing policy: to `local` in-process, to its broker remotely.

## In-process mode, one app, no broker

What `AddAuditPipeline` does in `InProcess` mode, written out by hand:

```csharp
services.ConfigureAuditDefault(configuration);
services.AddMessaging(m => m
    .AddOutbox().AddLocalPublisher()
    .AddLocalConsumer(c => c.SubscribeAuditDataChanges())
    .AddDelivery(d => { d.AddDeliveryPolicies(_ => { }); d.AddDeliveryProcessor<OrderingContext>("local"); }));
services.AddAuditOutbox<OrderingContext>();
// policy: "juice.audit" → PublisherKey "local"
```
