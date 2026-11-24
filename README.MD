# Juice.Audit

## What `UseAudit()` records

By default, an access log is recorded for:

- POST, PUT, PATCH and DELETE requests (`AuditFilterOptions.DefaultMethods`);
- any request whose action or page is marked `[AccessLogging]`, or that asks for one in code
  (`IAuditContextAccessor.AuditContext.RequestAccessLog()`), optionally only for the given status codes and, with
  `[AccessLogging(Methods = new[] { "GET" })]`, only for the given HTTP methods;
- any request that changed audited data, threw an exception, or ran past a time threshold (`[TimeLogging]`,
  `ExecutionTimeThreshold`).

Other requests, GETs included, are not recorded. Rules added in `UseAudit(appName, options => ...)` take precedence
over the default one; `options.Clear()` drops it, and with no rules at all every request is recorded.

```csharp
app.UseAudit("billing-api", options =>
{
    options.Include("/reports", "GET");     // also record report reads
    options.Exclude("/#/negotiate");        // never record SignalR negotiation
});
```

## Trace id

Every access log (`Req_TraceId`) and data change (`TraceId`) records the W3C trace id of the request that wrote it:
32 hexadecimal characters, the id OpenTelemetry back ends such as Juice Observability show. The response carries the
same id in the `X-Trace-Id` header, and the access log's metadata holds the request span's id under `SpanId`. Open
`{console}/traces/{traceId}` in the Juice Observability Console to see the request's spans and logs.

Without an active W3C activity, the id falls back to ASP.NET's `HttpContext.TraceIdentifier`. Before 10.4 the id was
the whole `traceparent` (`00-<trace id>-<span id>-01`); the Juice Observability Console accepts that form as well.

## Work outside an HTTP request: messages and mediator

`UseAudit()` opens an audit scope around each HTTP request. Two more scopes cover the work that runs without one, with
the same saving rules:

```csharp
services.ConfigureAuditDefault(configuration); // or ConfigureAuditGrpcClient(...)

// Mediator requests and notifications outside an audited request or message: background jobs, scheduled tasks,
// local-channel notifications. Works with any Juice.MediatR.
services.AddMediatorAudit(o => o.AppName = "billing-api");

// Integration event handlers: one record per message. Juice.Audit.Messaging package, Juice.Messaging 10.9 or later.
services.AddMessageAudit();
```

| Scope | Opened by | `Req_Method` | `Action`, `Req_Path` |
|---|---|---|---|
| HTTP request | `AuditMiddleware` (`UseAudit()`) | the HTTP method | the route |
| message | `MessageAuditBehavior`, an integration event pipeline behavior, around the event's handlers after the inbox check | `MESSAGE` | the event name |
| mediator request | `AuditRequestBehavior`, a mediator pipeline behavior outside idempotency and the transaction | `REQUEST` | the request type |
| notification | `AuditNotificationBehavior` | `NOTIFICATION` | the notification type |

Only the outermost scope opens: a command sent inside an HTTP request or a message joins its scope, so a message whose
handler sends two commands is one record with both commands' changes. Data change notifications (`AuditEvent`,
`DataEvent`) never open one: they report changes to the scope that is open.

A scope outside an HTTP request records:

| Field | Value |
|---|---|
| `Req_TraceId`, data change `TraceId` | the current W3C trace id. For a message, the trace of the request that raised the event, so the request in the first service and every consumer's changes share one id |
| `TenantId` | the event's tenant, or the current tenant (`ITenantAccessor`) |
| `User` | empty: a message or a job has no user; the first service's access record under the same trace id has it |
| `Res_Status` | 200; 500 when it threw (with `Res_Msg`, `Res_Err`), or for a message when no handler succeeded (with the first handler's exception); 400 for a failed `IOperationResult` |
| metadata | `SpanId`, the reasons it was saved (`DataAuditRequest`, `FilterMatched`, `TimeExceeded`, …); for a message `MessageId`, `Source`, `CorrelationId` |

It is saved, as a request is, when data changed, an access log or time log was requested through the
`IAuditContextAccessor`, `AuditScopeOptions.ExecutionTimeThreshold` was exceeded, or an `AuditScopeOptions.Filter` rule
matched its method and path, e.g. `o.Filter = new AuditFilterOptions().Include("OrderPlaced", "MESSAGE")`. Without a
filter, a scope that changed nothing saves nothing.

A data change with no scope open (an app without these scopes) is skipped, unless its transaction has an audited outbox
(see [Delivery guarantees](#delivery-guarantees)). Earlier versions threw there: the change was lost with a
`[DispatchAuditEvents] Error` warning, and the rest of that save's changes were not audited either.

A change made inside a transaction managed by Juice's `TransactionBehavior` is saved only if the transaction commits.
Otherwise it is dropped: the record of the attempt is still saved, with the number of dropped changes in its metadata
(`UncommittedChanges`). A transaction the app opens itself is not tracked: its changes are saved even if it rolls back.

A data change with no user of its own (a DbContext knows one only through `IHttpContextAccessor`) takes the user of its
scope.

## Delivery guarantees

Since 10.7 nothing is written inside the request. The app hands each record off and a collector (the audit service,
or the app itself in-process) stores it. The two kinds of record travel differently:

| Record | Transport | Guarantee |
|---|---|---|
| **Data change** (`DataAudit`) | Juice outbox, in the business transaction → broker (or the local transport) → collector | Stored if and only if the business change committed: at least once, deduplicated by id |
| **Access record** (`AccessLog`) | Bounded in-memory queue → background batches → collector | Best effort: lost when the process crashes or the queue is full, and every loss is counted |

Records get their id in the app (a time-ordered GUID), so a record sent twice is stored once. The collector writes a
batch in a few commands and skips the ids it already has.

### Setting it up

One call, in the app's messaging setup. `Audit:Mode` picks the mode and has no default: a missing or unknown value
fails at startup.

```csharp
services.AddMessaging(m => m
    .AddOutbox()
    .AddIdempotencyInMemory()                          // or the app's own idempotency store
    .AddPublishingPolicies(configuration.GetSection("Messaging:PublishingPolicies"))
    .AddAuditPipeline<OrderingContext>(configuration, o =>
    {
        o.ConfigureDb = db => db.DatabaseProvider = "PostgreSQL";   // InProcess only
        o.ConfigureOutbox = outbox => outbox.AppName = "ordering-api";
    }));
app.UseMessageContext("ordering-api");
app.UseAudit("ordering-api");
```

| `Audit:Mode` | Access records | Data changes | The app also needs |
|---|---|---|---|
| `Remote` | gRPC `LogBatch` to `Audit:Grpc:Address` (or `ConfigureGrpcClient`) | its broker | a `juice.audit` route to its broker |
| `InProcess` | written to its own audit database | the outbox delivers them to its own consumer (`local`) | a `juice.audit` route to `local` |

```json
"Messaging": { "PublishingPolicies": { "Rules": [
  { "Priority": 100, "Match": { "Domain": "juice.audit" }, "Publishers": [ { "Key": "local", "Destination": "juice.audit" } ] }
] } }
```

`TContext` is the app's `DbContextBase` with an outbox (`IOutboxContext`), changed under `TransactionBehavior`. An app
with several contexts adds `AddAuditOutbox<TOther>()` for each other one. An app that already runs the delivery
processor of `TContext` for `local` sets `AddLocalDeliveryProcessor = false`.

The collector (`test/Juice.Audit.Tests.GrpcHost` is one):

```csharp
services.ConfigureAuditGrpcHost(configuration);
services.AddAuditCollector();                      // processors and metrics, "Audit:Collector"
services.AddMessaging(m => m.AddIdempotencyInMemory());
services.AddEventBus(bus => bus.AddRabbitMQ(r => r
    .AddConsumer("audit", "juice.audit", "rabbitmq", c => c.SubscribeAuditDataChanges())));
app.MapAuditGrpcServer();                          // Log and LogBatch
app.MapJuiceAudit("/api/audit");
```

`ConfigureAuditGrpcClient()` and `ConfigureAuditDefault()` keep working without `AddAuditPipeline`. They now queue the
access records too, and the data changes stay best effort until the app adds `AddAuditOutbox<TContext>()`.

### Options

| Section | Option | Default | |
|---|---|---|---|
| `Audit:Export` | `QueueCapacity` | 10 000 | records waiting at most; a record that finds it full is dropped and counted |
| | `MaxBatchSize`, `MaxBatchAge` | 512, 2 s | a batch goes when it's full or its first record is that old |
| | `ExportTimeout` | 10 s | the deadline of every export call |
| | `MaxRetryAttempts`, `InitialBackoff`, `MaxBackoff` | 5, 1 s, 30 s | retries of a failed batch, doubling with ±20% jitter; an attempt that stores some records starts the count over |
| | `ShutdownFlushTimeout` | 5 s | how long the queue is sent at shutdown |
| | `DropWarningInterval` | 30 s | lost records are logged as one warning per interval |
| | `Synchronous` | `false` | export inside the request, for tests that read the database right after it |
| `Audit:Outbox` | `AppName` | entry assembly | the app name on each event |
| | `MaxEntriesPerEvent`, `MaxEventBytes` | 500, 512 KiB | larger sets are split; a larger change is cut (`$truncated`, `$originalSize`). The bytes of the event as the outbox stores and publishes it; keep it under the broker's message limit (NATS: 1 MiB) |
| | `RequireScope` | `false` | see below |
| `Audit:Collector` | `RedactHeaders`, `RedactMask` | `Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`; `***` | headers masked before storage, named in the record's `Redacted` metadata |
| | `MaxBodyLength` | 65 536 | longer bodies and errors are cut (`Truncated` metadata), and longer changes |

A collector adds its own processors with `AddAuditCollector().AddProcessor<T>()` (`IAuditRecordProcessor`, run in
`Order` with the built-ins at 100 and 200). One that throws or rejects fails only its record.

Its own gRPC calls are excluded from `UseAudit()` by default (`AuditFilterOptions.CollectorGrpcPath`), and handling
the outbox events opens no audit scope, so the collector doesn't audit itself.

A change made with no audit scope open (a background job without `AddMediatorAudit()`) now goes through the outbox
when its transaction has one, with the trace id of `Activity.Current`. Before 10.7 it was skipped. Set
`Audit:Outbox:RequireScope` to `true` to keep skipping it.

### Metrics

Meter `Juice.Audit`: collect it with `.WithMetrics(m => m.AddMeter("Juice.Audit"))`.

| Instrument | Where | Tags |
|---|---|---|
| `juice.audit.export.queue.size`, `.queue.capacity` | app | |
| `juice.audit.export.records` | app | `outcome` = `exported`, `duplicate`, `dropped`; `reason` = `queue_full`, `retries_exhausted`, `shutdown`, `rejected`, `unconfirmed` (a collector before 10.7) |
| `juice.audit.export.retries`, `.batches.failed` (`error`), `.duration` (`outcome`) | app | |
| `juice.audit.outbox.entries` | app | `outcome` = `outboxed`, `best_effort`; `reason` = `no_transaction`, `no_binding`, `no_route`, `tenant_mismatch`, `unmanaged` (a transaction the app opened itself), `error` |
| `juice.audit.collector.records` | collector | `kind` = `access`, `data`; `outcome` = `received`, `stored`, `duplicate`, `rejected`, `failed` |
| `juice.audit.collector.write.duration`, `.batch.size` | collector | `kind` |

An access record's metadata says where its scope's changes went: `OutboxedChanges`, `BestEffortChanges`, and
`UncommittedChanges` for those of a rolled back transaction.

### Upgrading

Upgrade the collector first, or with the apps: a 10.7 app sends `LogBatch`, and falls back to one call per record,
with one warning, against an older collector. Such a collector can't tell a resent record from a new one, so a record
it may have stored is never sent again, as before 10.7: one it refuses is lost as `rejected`, and one whose call is cut
off as `unconfirmed`. Only a record it never received (the collector can't be reached) is retried. Then, app by app, add `AddAuditPipeline` (or `AddAuditOutbox<TContext>()`)
and the `juice.audit` route, and subscribe the collector to it.

## Time logging

`[TimeLogging]` (optionally with a threshold in ms, and a `ScopeId`) and `AuditFilterOptions.ExecutionTimeThreshold`
ask for a request's timing. The steps measured by the request's `ITimeTracker` (Juice.Measurement) go to:

- **a time store**, when the app registers one (`AddMeasurementEFStores` or the gRPC store): `TimeSummaries` and
  `TimeRecords` under the request's trace id, with the `ScopeId` as the root scope id (the action name without one);
- **the log**, at `Information`, when no store is registered: the tracker's timing table, with the action and trace id.

When an OpenTelemetry tracer records the request, its span also gets `juice.audit.time_logged=true`; find these
requests in the Juice Observability Console with the trace attribute filter `juice.audit.time_logged=true`. In Juice
releases after 10.8 the tracker's scopes are spans of the request as well.

## Reading the records: API and pages

`MapJuiceAudit()` maps a read-only API over the audit records. The `@juice-js/audit` Angular pages
(`console/projects/juice-js/audit`) are built on it.

```csharp
app.MapJuiceAudit("/api/audit");                 // any authenticated user (the default policy)
app.MapJuiceAudit("/api/audit", "AuditReaders"); // or a policy of yours
```

| Endpoint | Returns |
|---|---|
| `GET access-logs` | access records, newest first; filters `user`, `action`, `path`, `method`, `status` (`404`, `4xx`, `error`), `app`, `tenant`, `traceId`, `minMs`, `restricted` |
| `GET access-logs/{id}` | one record: request, response, server, metadata, and how many data changes share its trace |
| `GET data-changes` | data changes, newest first; filters `table`, `schema`, `database`, `action`, `user`, `tenant`, `traceId`, `key` (text in the row's keys) |
| `GET data-changes/{id}` | one change, with its `OriginalValues` and `CurrentValues` |
| `GET traces/{traceId}` | the access records and data changes of one trace (a trace id, or a pre-10.4 `traceparent`) |
| `GET overview` | totals, status classes, top apps, users and tables, latest failures, slowest requests |

Lists and the overview take `from` and `to` (ISO 8601; the last 24 hours by default, at most 93 days). Lists also take
`limit` (1–200, default 50) and `cursor`, the `nextCursor` of the previous page. The records are read through
`AuditDbContext`, so a tenant sees only its own and the root tenant all of them. Map the API where that context is
registered: in the app with `ConfigureAuditDefault()`, or on the gRPC audit server (`ConfigureAuditGrpcHost()`).

## Building and releasing

`build.yml` builds and tests both sides on every push to `main`, `release/**` and `dev/**`. On a `release/*` branch,
once build and test pass, it publishes alphas of each side the push changed:

| Package | Alpha | Version from |
|---|---|---|
| `Juice.Audit.*` | `<VersionPrefix>-alpha.<run>` on GitHub Packages | `VersionPrefix` in `Directory.Build.props` |
| `@juice-js/audit` | `<version>-alpha.<run>` on npm, dist-tag `alpha` | `version` in `console/projects/juice-js/audit/package.json` |

Run Build by hand on a `release/*` branch to publish both. Releases come from a GitHub release on `main` or a
`release/*` branch (`release.yml`). The tag names the package and its version, without a "v":

| Tag | Publishes |
|---|---|
| `api-10.5.0` | `Juice.Audit.*` to nuget.org (trusted publishing) |
| `console-1.0.0` | `@juice-js/audit` to npm. Dist-tag `latest` from `main`, `release-1.x` from `release/1.x` |

A pre-release version (`api-10.5.0-beta.1`, `console-1.1.0-rc.1`) is published as a pre-release, with its label as the
npm dist-tag. A NuGet beta or rc references the Juice packages of the same stage.

## License

[MIT](LICENSE)
