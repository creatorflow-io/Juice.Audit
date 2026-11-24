# Implementation Plan: Audit Collector Pipeline (app → collector → db)

**Branch**: `002-audit-collector-pipeline` | **Date**: 2026-11-19 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/002-audit-collector-pipeline/spec.md`

## Summary

Audit stops saving records inside the request that produced them. The app now has two ways to send them, and both
lead to a collector (the audit service) that batches, processes and writes them.

- **Data changes** made under `TransactionBehavior` are sent through the host's outbox. An `IDataChangeSink`,
  registered per context with `AddAuditOutbox<TContext>()`, adds a single `DataChangesAudited` event per transaction
  to that context's `IOutboxService<TContext>` while the transaction is dispatching its events. The event commits or
  rolls back with the business change (research R1–R3). When the outbox can't be used, the change falls back to
  today's best-effort path, and each case is counted (R4).
- **Access records** are put on a bounded in-memory queue. A background exporter sends them in batches, with a
  deadline, retries with backoff and a flush on shutdown. It uses a new `LogBatch` gRPC call, or calls the collector
  in-process. Drops are counted (R9, R10).
- **The collector** checks the access record batches and the data change messages for duplicate ids (the existing
  primary key, now assigned in the app; R8), runs its processors (header redaction, truncation), writes in bulk
  through EF, and reports metrics. It never audits its own work (R11).

No schema migration. No breaking public API. Released as `Juice.Audit.*` 10.7.0.

## Technical Context

**Language/Version**: C# latest; `net6.0;net8.0;net9.0;net10.0` (libraries and hosts), tests on `net8.0;net9.0;net10.0`
**Primary Dependencies**: Juice 10.10 (Juice.MediatR, Juice.EF, Juice.Messaging, Juice.Messaging.Outbox,
Juice.Messaging.Local for in-process mode), Grpc.Net.ClientFactory / Grpc.AspNetCore.Server 2.76,
`System.Threading.Channels`, `Microsoft.Extensions.Hosting.Abstractions` (new reference in `Juice.Audit`, for the
export worker)
**Storage**: the existing `AuditDbContext` on PostgreSQL or SQL Server. Tables unchanged.
**Testing**: xUnit v3 and FluentAssertions. Unit tests extend `test/Juice.Audit.Scopes.Tests` (`AuditScopeFixture`).
Database and broker tests in `test/Juice.Audit.Tests` use `IgnoreOnCIFact`.
**Target Platform**: ASP.NET Core hosts and worker services (Linux and Windows)
**Project Type**: NuGet libraries plus test hosts
**Performance Goals**: the hand-off adds ≤ 2 ms or 5% p99 (SC-002); 1 000 audited requests/s on one collector with
writes per batch (SC-003)
**Constraints**: never throw to the host or block it (constitution VI); every loss counted; the proto changes stay
compatible; EF only, no raw SQL
**Scale/Scope**: about 25 new or changed source files across 5 packages, about 6 test classes, the README, and a
sample context in the test host

## Constitution Check

*GATE: before Phase 0 and again after Phase 1. Both PASS.*

| Principle | Pre-design | Post-design | Notes |
|---|---|---|---|
| I. Lightweight & Dual-Architecture | PASS | PASS | `AddAuditPipeline<TContext>(configuration)` picks remote or in-process from `Audit:Mode`, so switching needs no code change; both modes use the same exporter, sink and collector code. |
| II. Library-First Composability | PASS | PASS | No new package. The outbox code goes in `Juice.Audit.Messaging`, which already references Juice.Messaging. `Juice.Audit.Api` sees only the `IDataChangeSink` abstraction from `Juice.Audit`. The layering holds. |
| III. DDD + CQRS | PASS | PASS | Collector writes go through `SaveAuditBatchCommand` on `Juice.MediatR.IMediator`. `TransactionBehavior`'s order is untouched; audit joins it through notifications and the outbox. |
| IV. Reliable Messaging via Outbox | PASS | PASS (justified) | Data changes go through `IOutboxService<TContext>` and `IMessagePublishingPolicy`, with idempotent consumers. Access records use gRPC, not the outbox. That's allowed by Principle IV of this repo (it applies to "saved with a business change"), and recorded in Complexity Tracking. |
| V. Multi-Tenancy First | PASS | PASS | The outbox event carries the scope's tenant, and the consumer writes in that tenant. gRPC batches are written as the root tenant with each record's own `TenantId` (R7). |
| VI. Audit Never Breaks the Host | PASS | PASS | Enqueue only; deadline on every call; the sink never throws (R4.5); metrics and rate-limited warnings for every drop. |
| Quality gates | — | PASS | Migrations: none needed (R8). `IgnoreOnCIFact` on infrastructure tests. `[InitializeMessageContext]` on messaging tests. README documents the changed guarantees. |

## Project Structure

### Documentation (this feature)

```text
specs/002-audit-collector-pipeline/
├── plan.md              # this file
├── research.md          # Phase 0: decisions R1–R13, risks
├── data-model.md        # Phase 1: ids, metadata keys, AuditContext states, event and batch shapes
├── quickstart.md        # Phase 1: how to check it end to end
├── contracts/
│   ├── grpc.md          # proto additions (ids, LogBatch)
│   ├── public-api.md    # registration, options, extension points
│   └── metrics.md       # meter Juice.Audit instruments
├── checklists/requirements.md
└── tasks.md             # Phase 2 (/speckit.tasks), not created here
```

### Source Code (repository root)

```text
src/
├── Juice.Audit/                                   # abstractions + app-side exporter
│   ├── AuditId.cs                                 NEW  time-ordered Guid (v7; Guid.CreateVersion7 on net9+)
│   ├── AuditContext.cs                            CHG  outboxed-pending / OutboxedChangeCount (data-model.md)
│   ├── IDataChangeSink.cs                         NEW
│   ├── Domain/AccessLogAggregate/AccessLog.cs     CHG  Id = AuditId.New() in ctor
│   ├── Domain/DataAuditAggregate/DataAudit.cs     CHG  Id = AuditId.New() in ctor
│   ├── Domain/Commands/SaveAuditBatchCommand.cs   NEW  batch + per-record results
│   ├── Domain/CommandHandlers/SaveAuditBatchCommandHandler.cs  NEW  dedupe by id → processors → AddRange → SaveChanges
│   ├── Domain/CommandHandlers/SaveAuditInfoCommandHandler.cs   CHG  delegates to the batch handler (one item)
│   ├── Export/AuditExportOptions.cs               NEW
│   ├── Export/AuditExportItem.cs                  NEW
│   ├── Export/AuditExportQueue.cs                 NEW  singleton bounded Channel, DropWrite + counter
│   ├── Export/AuditExportWorker.cs                NEW  BackgroundService: batch by size/age, retry, flush
│   ├── Export/IAuditExporter.cs                   NEW
│   ├── Export/AuditExportMetrics.cs               NEW  meter "Juice.Audit" (contracts/metrics.md)
│   ├── Services/QueuedAuditService.cs             NEW  IAuditService → enqueue (or inline when Synchronous)
│   ├── Services/GrpcAuditExporter.cs              NEW  LogBatch + deadline; Unimplemented → unary Log (R10)
│   ├── Services/InProcessAuditExporter.cs         NEW  scope → mediator SaveAuditBatchCommand
│   ├── Services/GrpcAuditService.cs               CHG  message mapping moved to a shared mapper; not used by the exporter (no deadline)
│   └── DependencyInjection/AuditServiceCollectionExtensions.cs CHG  AddAuditExport(), exporter registrations
├── Juice.Audit.Api.Contracts/Protos/audit.proto   CHG  ids 11/27, LogBatch (contracts/grpc.md)
├── Juice.Audit.Api/
│   ├── NotificationHandlers/DataEvenNotificationtHandler.cs  CHG  sink first, then fallback; works without scope (R5)
│   ├── NotificationHandlers/TransactionCommittedHandler.cs   CHG  commit outboxed counts
│   ├── Scopes/AuditRecorder.cs                    CHG  metadata OutboxedChanges / BestEffortChanges
│   ├── Middleware/AuditFilterOptions (existing)   CHG  default exclusion of the audit gRPC path (R11)
│   ├── Grpc.Services/AuditGrpcServer.cs           CHG  LogBatch → SaveAuditBatchCommand (root tenant)
│   ├── Collector/IAuditRecordProcessor.cs         NEW
│   ├── Collector/AuditRecordEnvelope.cs           NEW
│   ├── Collector/HeaderRedactionProcessor.cs      NEW
│   ├── Collector/TruncationProcessor.cs           NEW
│   ├── Collector/AuditCollectorOptions.cs         NEW
│   ├── Collector/AuditCollectorMetrics.cs         NEW
│   └── Extensions/ApiAuditServiceCollectionExtensions.cs  CHG  ConfigureAudit* use the exporter; AddAuditCollector()
├── Juice.Audit/Domain/IAuditWriteErrorClassifier.cs NEW  UniqueViolation | Transient | Other (no EF in Juice.Audit)
├── Juice.Audit.EF/Repositories/*.cs               CHG  GetExistingIdsAsync(ids), AddRange without save, ClearTracked
├── Juice.Audit.EF/AuditWriteErrorClassifier.cs    NEW  SQL Server 2627/2601, PostgreSQL 23505, IsTransient
└── Juice.Audit.Messaging/
    ├── DataChangesAudited.cs                      NEW  event + DataChangeEntry (data-model.md)
    ├── Outbox/AuditOutboxOptions.cs               NEW
    ├── Outbox/AuditOutboxBinding.cs               NEW  per TContext: claims a TransactionId, exposes IOutboxService<TContext>
    ├── Outbox/OutboxDataChangeSink.cs             NEW  IDataChangeSink: route check, one event per tx, split (R3, R4, R6)
    ├── Collector/DataChangesAuditedHandler.cs     NEW  IIntegrationEventHandler → SaveAuditBatchCommand
    ├── MessageAuditBehavior.cs                    CHG  pass DataChangesAudited through (R11)
    └── DependencyInjection/*.cs                   CHG  AddAuditOutbox<TContext>(), SubscribeAuditDataChanges(),
                                                        AddAuditPipeline<TContext>(configuration): mode from Audit:Mode

test/
├── Juice.Audit.Scopes.Tests/
│   ├── ExportQueueTests.cs                        NEW  batching, drop, retry/backoff, flush, synchronous
│   ├── OutboxSinkTests.cs                         NEW  T-OUTBOX-1..6 below
│   ├── CollectorTests.cs                          NEW  dedupe, processors, partial rejection
│   └── TransactionAuditTests.cs                   CHG  run with and without a sink
├── Juice.Audit.Tests/
│   ├── OutboxCrashTests.cs                        NEW  SC-001 (IgnoreOnCIFact; SQLite or a real provider)
│   ├── GrpcBatchTests.cs                          NEW  LogBatch, unary fallback, deadline
│   └── IdempotentWriteTests.cs                    NEW  both providers (IgnoreOnCIFact)
├── Juice.Audit.Tests.Host/                        CHG  SampleOrderContext + TransactionBehavior subclass; remote mode
└── Juice.Audit.Tests.GrpcHost/                    CHG  AddAuditCollector + data change consumer
```

**Structure Decision**: keep the existing package layout (constitution II). The exporter goes in `Juice.Audit`,
because `IAuditService` lives there and both modes need it. The outbox code goes in `Juice.Audit.Messaging`, the only
package that may reference Juice.Messaging. The collector processors go in `Juice.Audit.Api` next to the gRPC server.
The data change consumer goes in `Juice.Audit.Messaging`.

## Design notes

### App: write path

```text
AuditEvent<T>  (TransactionBehavior dispatch, before SaveEventsAsync)
  └─ DataEvenNotificationtHandler<T>
       ├─ sink.TryAppend(tx, entry) ── true ──► AuditOutboxBinding<TContext> owns tx
       │                                        └─ OutboxDataChangeSink: open event for (tx, tenant)?
       │                                             no  → new DataChangesAudited, outbox.AddEventAsync(evt)
       │                                             yes → evt.Entries.Add(entry)  (serialized later, R3)
       │                                        AuditContext.AddOutboxedPending(tx)
       └─ false ──► today's path (uncommitted(tx) / AuditEntries), BestEffort mark
TransactionCommitted(tx) ─► AuditContext: pending(tx) → OutboxedChangeCount; uncommitted(tx) → AuditEntries
Scope end ─► AuditRecorder.PersistAsync ─► QueuedAuditService.Enqueue(access record + best-effort entries)
AuditExportWorker ─► IAuditExporter (gRPC LogBatch | in-process SaveAuditBatchCommand)
```

### Collector: write path

```text
LogBatch / DataChangesAuditedHandler
  └─ SaveAuditBatchCommand(items)
       1. existing = repo.GetExistingIdsAsync(all ids)              → Duplicate
       2. processors in Order (redact, truncate, custom)             → Rejected per record
       3. AddRange(rest); SaveChangesAsync()                         → Stored
          on unique violation: retry one by one; conflict → Duplicate
          on transient DB error: Retryable (gRPC) / throw (message → broker retry)
```

### Tests that lock in the guarantees

| Id | Test | Guards |
|---|---|---|
| T-OUTBOX-1 | A committed change produces exactly one outbox event with all entries; the access record has `OutboxedChanges = n` and no entries. | FR-001, FR-002 |
| T-OUTBOX-2 | A rolled-back change leaves no outbox row; `UncommittedChanges = n`. | FR-005 |
| T-OUTBOX-3 | Entries appended after `AddEventAsync` (second dispatch round) are in the saved payload. | R3 (core behavior) |
| T-OUTBOX-4 | Each fallback reason (no tx, no binding, no route, tenant mismatch, sink exception) carries the change best effort and increments `juice.audit.outbox.entries{outcome=best_effort,reason=…}`. | FR-006, R4 |
| T-OUTBOX-5 | Over `MaxEntriesPerEvent` the entries are split into parts; an entry over `MaxEventBytes` is truncated and marked. | R6 |
| T-OUTBOX-6 | The same event consumed twice stores each entry once. | FR-016, SC-005 |
| T-EXPORT-1 | With a hanging exporter, `PersistAuditInformationAsync` returns in < 1 ms and the scope completes. | FR-007, SC-002 |
| T-EXPORT-2 | Queue full → `dropped{reason=queue_full}` equals the records refused; one warning per interval. | FR-010, FR-027, SC-004 |
| T-EXPORT-3 | `Unimplemented` from `LogBatch` → unary fallback, nothing dropped. | R10 |

## Rollout

1. **10.7.0-alpha** on `release/10`: all of the above. `ConfigureAuditGrpcClient` and `ConfigureAuditDefault` switch to
   the queued exporter. Data changes stay best effort until the app adds `AddAuditOutbox<TContext>()`, so upgrading the
   package alone doesn't change where the data changes go.
2. Upgrade the collector (the gRPC host) before or with the apps. Older collectors keep working through the unary
   fallback.
3. In each app, add `AddAuditOutbox<TContext>()` and a `juice.audit` publishing route, and subscribe the collector.
4. README: a new "Delivery guarantees" section (the table from the spec), the exporter options, the metrics, and the
   no-scope behavior change (R5).

## Follow-ups (not in this feature)

- Core: let `AuditEvent` carry the context type, or add a current-outbox accessor, so `AuditOutboxBinding` doesn't
  have to compare transactions (R2).
- Core: outbox messages after a replay by the execution strategy (research, Risks).
- Time logging through the same exporter.
- Batching across messages in the collector, if the data change rate calls for it (R12).

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Access records use gRPC, not the outbox (core constitution IV: "integration messages through the outbox") | Access records are made after the transaction and for requests without one (GETs, failed requests); there's no transaction to join. They're best effort by decision (spec). | Writing them to the outbox would add an outbox transaction per request, the write we're removing from the request path. |
| A fallback path for data changes in addition to the outbox | Changes made outside `TransactionBehavior` or in contexts without an outbox exist today, and dropping their audit would be a regression. | Outbox-only would silently stop auditing those changes. |
| `AuditOutboxBinding` compares the current transaction id instead of being told the context | Core has no transaction → outbox accessor (R2). | A core change first would block this feature on a core release. Listed as a follow-up. |
