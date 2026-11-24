# Research: Audit Collector Pipeline

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-11-19

Facts about Juice core were read from `D:\Workspaces\Juice\Juice\core\src` (Juice 10.10). Paths below are relative to
that folder unless they start with `src/` or `test/` (this repo).

---

## R1. Can an audit handler add to the outbox inside the business transaction?

**Decision**: Yes. The handler of `AuditEvent<T>` adds a `DataChangesAudited` integration event to the
transaction's `IOutboxService<TContext>` while `TransactionBehavior` is dispatching events.

**Rationale**:
- `TransactionBehavior` (`Juice.MediatR.Behaviors/TransactionBehavior.cs:162-186`) runs, inside one transaction:
  SaveChanges (:164) → `DispatchEventsUntilStableAsync` (:166) → `_outboxService.SaveEventsAsync(transactionId)`
  (:167) → Commit (:179). Audit events are published in the dispatch step (`Juice.EF/Extensions/MediatorExtensions.cs:91`),
  before the outbox is saved.
- `OutboxEventService.AddEventAsync` only appends to an in-memory list (`Juice.Messaging.Outbox/Internal/OutboxEventService.cs:43-51`).
  `SaveEventsAsync` serializes the list and writes it with the transaction id and shares the transaction
  (`OutboxRepository.cs:38-78`).
- Adding to the outbox changes no tracked entity, so it doesn't start another dispatch round. Core already does this
  in a test: `core/test/Juice.EF.Tests.Shared/EventHandlers/ContentInsertedEventHandler.cs:10-24`.

**Alternatives considered**:
- *Write to the outbox on `TransactionCommitted`*: too late, the transaction has committed. That is today's problem.
- *Write audit rows into the business database in the same transaction*: this needs the audit schema in every app,
  which is what FR-022 and SC-006 remove.

---

## R2. Which outbox? Mapping a `TransactionId` to `IOutboxService<TContext>`

**Decision**: The app registers each audited context explicitly with `AddAuditOutbox<TContext>()`, in
`Juice.Audit.Messaging`. Each call adds a scoped `AuditOutboxBinding<TContext>`. Given an event's `TransactionId`,
the binding resolves the scoped `TContext` and claims the event when
`context.Database.CurrentTransaction?.TransactionId` equals that id. It then appends to that scope's
`IOutboxService<TContext>`.

**Rationale**:
- Core has no registry or ambient accessor from a transaction id to a context or outbox. The non-generic
  `IOutboxService` isn't registered (`OutboxServiceCollectionExtensions.cs:18`, open generic only).
- `TransactionBehavior<TRequest, TContext>` is already subclassed per context in every app, so registering one
  binding per context fits how apps are wired.
- The scoped `TContext` that the binding resolves is the same instance that raised the event and that the
  behavior will save. The outbox list is per scope and per `TContext`, so the entry lands in the right transaction.

**Alternatives considered**:
- *Change core so `AuditEvent` carries the context type, or add an "current outbox" accessor*: cleaner, but it ties
  this feature to a core release. It's listed as a follow-up. The binding can switch to it later without changing
  the public API.
- *Resolve every registered context and compare transactions*: creates unused contexts and guesses. Rejected.

---

## R3. One event per transaction, filled across dispatch rounds

**Decision**: On the first entry of a transaction (per tenant, see R7), the sink creates a `DataChangesAudited` event
and calls `AddEventAsync` once. Later entries of the same transaction are appended to that same object. When the
event reaches its size limit (R6), a new event is started. The open events are tracked per transaction in the
scoped sink.

**Rationale**:
- `SaveEventsAsync` serializes events when it saves them (`OutboxEventService.cs:105`), after every dispatch round, so
  entries appended after `AddEventAsync` are included. This relies on internal ordering, so a test locks it in
  (T-OUTBOX-3 in plan.md).
- Audit events are published one at a time: each is awaited in a `foreach` (`MediatorExtensions.cs:160-169`). The
  appends aren't concurrent. The sink still takes a lock, which is cheap insurance.

**Alternatives considered**: *one event per changed row* is simple, but it means one outbox row and one broker
message per row. Rejected, as FR-002 requires.

---

## R4. When the outbox path can't be used: detect and fall back

**Decision**: The sink returns "not taken" and the entry goes to the best-effort path (today's behavior, held until
`TransactionCommitted`) when any of these is true:

1. `TransactionId` is null (a plain `SaveChanges`; `MediatorExtensions.cs:165-167` sets it only for a managed context).
2. No registered binding claims the transaction (the context has no `AddAuditOutbox<T>()`, or no outbox).
3. The publishing policy resolves no route for `DataChangesAudited` other than `local-channel`. `SaveEventsAsync`
   skips such messages silently (`OutboxEventService.cs:78-82`), so the sink checks with `IMessagePublishingPolicy`
   before taking the entry. The result is cached per tenant for the scope.
4. The entry's tenant differs from the scope's tenant (R7).
5. Anything throws. Core swallows exceptions from audit handlers (`MediatorExtensions.cs:177-184`) and skips the rest
   of that round, so the sink must never throw.

Each fallback marks the entry `Delivery = BestEffort` in its metadata (FR-006). At startup, the app logs once for each
audited context that has no binding.

---

## R5. Data change with no audit scope open

**Decision**: An entry taken by the outbox doesn't need an audit scope. The trace id comes from `Activity.Current`,
the user from `AuditRecord.User`, and the tenant from the event or `ITenantAccessor`. Without a scope, the
best-effort path stays as it is today: the change is skipped, with a debug log.

**Rationale**: The outbox guarantee shouldn't depend on whether the app registered `AddMediatorAudit()`. This is a
behavior change: background jobs without a scope start producing data change records. The README states it. An app
can turn it off with `AuditOutboxOptions.RequireScope = true`.

---

## R6. Event size limit

**Decision**: An event is closed when it has `MaxEntriesPerEvent` entries (default 500), or when it would exceed
`MaxEventBytes` (default 512 KiB) as the outbox stores it: each entry is measured with the outbox's serializer, so the
second escaping of `Changes`, the type names and every field count. A single entry larger than the limit has its
`Changes` cut to the most that fits, as `{"$truncated":true,"$originalSize":n,"$partial":"…"}`.

*Revised 2026-11-24*: the first version summed the raw `Changes` and `Kvps` lengths; stored events measured 1.06–1.75×
that (escaping, about 400 bytes per entry of other fields and `$type`), so the 512 KiB events reached 895 KiB.

**Rationale**: The outbox has no size limit of its own (`PayloadBytes` is `varbinary(max)` / `bytea`). The transports
define none in Juice either, so broker defaults apply (RabbitMQ 128 MiB by default; NATS `max_payload` 1 MiB by
default). 512 KiB stays under the strictest common default, NATS. Both values can be configured.

---

## R7. Tenants

**Decision**:
- **App side**: the outbox event takes the scope's tenant (`OutboxEventService` stamps `x-tenant-id` from
  `ITenantAccessor`, :97 and :114). Only entries whose tenant is the scope's tenant (or null) are taken. Others fall
  back (R4.4). In practice one transaction belongs to one tenant.
- **Collector, data changes**: the consumer resolves the tenant from `x-tenant-id`, as every Juice consumer does.
  `AuditDbContext` then writes in that tenant. Entries match it, so `TenantMismatchMode.Throw` doesn't trigger.
- **Collector, access batches over gRPC**: these are written as the root tenant, as `AuditGrpcServer` does today.
  The root tenant keeps each record's own `TenantId` (`AuditDbContext`, `SharingType.Global`). This covers mixed
  tenant batches (FR-017).

---

## R8. Idempotent writes without a migration

**Decision**:
- Ids are generated in the app when the record is created: `AuditId.New()`, a time-ordered GUID. It uses
  `Guid.CreateVersion7()` on .NET 9 and later, and a small v7 implementation on net6 and net8.
- The collector deduplicates on the existing primary key. For each batch it reads
  `SELECT Id FROM … WHERE Id IN (@ids)`, then adds only the missing records with `AddRange` and one `SaveChanges`.
- If a concurrent writer causes a key conflict (`DbUpdateException` with a unique violation), the batch is retried
  record by record, and conflicts on a single record count as duplicates.

**Rationale**:
- `Id` is already the `Guid` primary key of both tables, set with `ValueGeneratedOnAdd`, which EF generates in the app
  (`src/Juice.Audit.EF/EntityTypeConfiguration/*.cs`). Setting it earlier changes no column, index or migration.
  Existing rows are untouched.
- `IN` plus `AddRange` works the same on both providers and keeps to EF, as the constitution requires (no raw SQL
  outside migrations). EF batches the inserts, so a batch costs a few round trips, not one per record.
- Old clients send no id. The server generates one, as today.

**Alternatives considered**: `ON CONFLICT DO NOTHING` / `MERGE` would need raw SQL per provider. `EFCore.BulkExtensions`
adds a dependency and licensing questions. Both rejected for now.

---

## R9. Access record exporter defaults

| Option | Default | Why |
|---|---|---|
| `QueueCapacity` | 10 000 | About 10 s at 1 000 req/s (SC-003). A few MB of memory. |
| `MaxBatchSize` | 512 | The OTel `BatchExportProcessor` default. |
| `MaxBatchAge` | 2 s | Keeps records visible in the console within seconds. |
| `ExportTimeout` | 10 s | The deadline for each gRPC call (FR-009). |
| `MaxRetryAttempts` | 5 | With backoff of 1 s × 2ⁿ ± 20% jitter, capped at 30 s, that's about 1 minute of outage before the batch is dropped. |
| `ShutdownFlushTimeout` | 5 s | Fits inside the default 30 s host shutdown. |
| `Synchronous` | `false` | `true` exports inside `PersistAuditInformationAsync`, for tests (FR-024). |

**Queue**: `Channel.CreateBounded` with `FullMode = DropWrite` and a counter on `TryWrite` failure (FR-010). Records
already queued are kept.

---

## R10. Old collector, new client

**Decision**: When `LogBatch` returns `StatusCode.Unimplemented`, the gRPC exporter switches, for the life of the
process, to the unary `Log` call (one call per record, still in the background with a deadline). It logs one warning
that names the minimum collector version (10.7). No record is dropped because of the version mismatch.

**Alternatives considered**: failing at startup would block deploys while the collector is being upgraded. Rejected.

---

## R11. The collector must not audit itself

**Decision**:
- `MessageAuditBehavior` passes `DataChangesAudited` straight through.
- The default `AuditFilterOptions` exclude the path `/juice.audit.grpc.AuditGrpcService/`.
- `AuditDbContext` entities aren't auditable (unchanged), so they raise no `AuditEvent`.

---

## R12. Batching in the collector for data changes

**Decision**: one write per message, not per row. Juice consumers get one event per handler call
(`IntegrationEventDispatcher.cs:92-121`; there's no batch consume), and one message already carries the entries of a
transaction (R3). That meets SC-003, which concerns access records. Batching across messages isn't needed now.

---

## R13. Serialization of the event

**Decision**: `DataChangesAudited` holds only strings and primitives: the entry fields as `DataAudit` stores them,
with `Kvps` and `Changes` already JSON. It doesn't hold `AuditRecord.Entity`.

**Rationale**: the Juice message serializer is Newtonsoft with `TypeNameHandling.All` and a known-types binder
limited to the `Juice` assembly prefix (`Juice.Messaging/MessageSerializer.cs:32, 98-110`). The event lives in
`Juice.Audit.Messaging`, so its type is allowed. Leaving out the entity keeps app CLR types out of the payload.

---

## Risks

- **Late serialization (R3)** is core behavior, not a documented contract. T-OUTBOX-3 detects a change.
- **Execution strategy retries**: if `ResilientTransaction` replays the transaction, dispatch skips events that were
  already dispatched (`MediatorExtensions.cs:87`). The outbox list was cleared after the first save (:139). This
  affects every outbox user, not just audit. It should be raised with core; it isn't solved here.
- **Behavior change (R5)**: apps start recording data changes made by background work without a scope.
