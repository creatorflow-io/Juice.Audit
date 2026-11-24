---
description: "Task list for the audit collector pipeline"
---

# Tasks: Audit Collector Pipeline (app → collector → db)

**Input**: Design documents from `specs/002-audit-collector-pipeline/`
**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md),
[contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: included. The spec gives each story an independent test, and plan.md names the tests that lock in the
guarantees (T-OUTBOX-1..6, T-EXPORT-1..3). Write each story's tests first and see them fail.

**Organization**: by user story (spec.md). US1 and US2 are both P1; either can be the MVP, and they don't depend on
each other once Phase 2 is done.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1–US6 from spec.md
- Paths are relative to the repository root `services/audit/`

## Conventions for every task

- Follow `.specify/memory/constitution.md`. In particular, audit code must never throw to the host or wait on the
  network without a deadline (VI).
- Infrastructure tests (real database, broker, gRPC host) use `IgnoreOnCIFact`. Messaging tests use
  `[InitializeMessageContext]`.
- Builds must pass for every target framework: `dotnet build Juice.Audit.sln`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: package references and test scaffolding

- [X] T001 Add `Microsoft.Extensions.Hosting.Abstractions` (version `$(MicrosoftExtensionsVersion)`) and `System.Threading.Channels` where the target framework lacks it (net6) to `src/Juice.Audit/Juice.Audit.csproj`, for the export worker
- [X] T002 [P] Add `Juice.Messaging.Outbox` (`$(JuiceMessagingVersion)`) to `src/Juice.Audit.Messaging/Juice.Audit.Messaging.csproj`, for `IOutboxService<TContext>`, `IMessagePublishingPolicy` and `IConsumerBuilder`; and `Juice.Messaging.Local` (`$(JuiceMessagingVersion)`), for `AddLocalPublisher` / `AddLocalConsumer` in `AddAuditPipeline` (T070a)
- [X] T003 [P] Add `Microsoft.EntityFrameworkCore.Sqlite` (`$(EFVersion)`), `Juice.Messaging.Outbox.EF` and `Juice.Messaging.Local` (`$(JuiceMessagingVersion)`) to `test/Juice.Audit.Scopes.Tests/Juice.Audit.Scopes.Tests.csproj`, for outbox tests on SQLite with the local transport
- [X] T004 [P] Bump `VersionPrefix` to `10.7.0` in `Directory.Build.props`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: ids set in the app, the batch save command with deduplication, the proto additions and the meter. US1,
US2, US4 and US5 all build on these.

**⚠️ CRITICAL**: no user story work before this phase is done.

- [X] T005 [P] Create `AuditId.New()` in `src/Juice.Audit/AuditId.cs`: a time-ordered GUID, `Guid.CreateVersion7()` on `NET9_0_OR_GREATER`, otherwise a v7 layout (48-bit Unix ms timestamp, version and variant bits, random rest) (research R8)
- [X] T006 [P] Unit test `AuditId` in `test/Juice.Audit.Scopes.Tests/AuditIdTests.cs`: version nibble 7, variant bits, monotonic ordering across 10 000 calls in the same ms bucket, uniqueness
- [X] T007 Set `Id = AuditId.New()` in the constructors of `src/Juice.Audit/Domain/AccessLogAggregate/AccessLog.cs` and `src/Juice.Audit/Domain/DataAuditAggregate/DataAudit.cs` (keep the public setter; EF keeps a set id with `ValueGeneratedOnAdd`) (FR-003, FR-012)
- [X] T008 [P] Add `GetExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken)`, an `AddRange` that doesn't save, and `ClearTracked()` (detach the entities of a failed save) to the repository interfaces in `src/Juice.Audit/Domain/` (`IAccessLogRepository`, `IDataAuditRepository`) and implement them in `src/Juice.Audit.EF/Repositories/AccessLogRepository.cs` and `DataAuditRepository.cs` (`Where(x => ids.Contains(x.Id)).Select(x => x.Id)`, chunks of 1 000 ids)
- [X] T009 Create `SaveAuditBatchCommand` (items: access record + data audits; data-only items allowed) and the result type with a per-record `Stored | Duplicate | Rejected(reason) | Retryable(reason)` in `src/Juice.Audit/Domain/Commands/SaveAuditBatchCommand.cs` (data-model.md "Batch result")
- [X] T009a [P] Create `IAuditWriteErrorClassifier` (`AuditWriteError Classify(Exception ex)` → `UniqueViolation | Transient | Other`) in `src/Juice.Audit/Domain/IAuditWriteErrorClassifier.cs`, and implement it in `src/Juice.Audit.EF/AuditWriteErrorClassifier.cs` (unwrap `DbUpdateException`; SQL Server 2627/2601 and PostgreSQL 23505 → `UniqueViolation`; `SqlException.IsTransient`, `NpgsqlException.IsTransient`, timeouts → `Transient`); register it in `Juice.Audit.EF`'s DI extension. `Juice.Audit.EF` already references both providers, and `Juice.Audit` has no EF reference
- [X] T010 Implement `SaveAuditBatchCommandHandler` in `src/Juice.Audit/Domain/CommandHandlers/SaveAuditBatchCommandHandler.cs`: existing ids → `Duplicate`; an empty `IEnumerable<IAuditRecordProcessor>` hook point (filled in US5); `AddRange` the rest; one `SaveChangesAsync`; errors classified by `IAuditWriteErrorClassifier` as `Transient` → `Retryable` (no EF reference in `Juice.Audit`) (depends on T008, T009, T009a)
- [X] T011 Make `SaveAuditInfoCommandHandler` in `src/Juice.Audit/Domain/CommandHandlers/SaveAuditInfoCommandHandler.cs` delegate to the batch handler with one item, so both paths share the deduplication; register both in `src/Juice.Audit/DependencyInjection/AuditServiceCollectionExtensions.cs` (`AddAuditCommandHandlers`)
- [X] T012 [P] Extend `src/Juice.Audit.Api.Contracts/Protos/audit.proto` as in contracts/grpc.md: `DataAuditMessage.id = 11`, `AccessLogMessage.id = 27`, `AuditBatchRequest`, `AuditRecordResult` (with `Outcome`), `AuditBatchResult`, `rpc LogBatch`; no renumbering
- [X] T013 Move the domain ↔ proto mapping out of `src/Juice.Audit/Services/GrpcAuditService.cs` and `src/Juice.Audit.Api/Grpc.Services/AuditGrpcServer.cs` into `src/Juice.Audit/Services/AuditMessageMapper.cs` (both directions, including the new `id` fields; an empty id means the server generates one) (depends on T012)
- [X] T014 [P] Create `src/Juice.Audit/Export/AuditExportMetrics.cs`: a singleton holding meter `Juice.Audit` and every app-side instrument in contracts/metrics.md (`juice.audit.export.*`, `juice.audit.outbox.entries`), with typed `Record…` helpers so callers never build tags by hand
- [X] T015 Unit test `SaveAuditBatchCommandHandler` with fake repositories in `test/Juice.Audit.Scopes.Tests/SaveAuditBatchTests.cs`: duplicates are skipped, new records saved once, a data-only item works, a transient error gives `Retryable`

**Checkpoint**: ids are set in the app, the batch save deduplicates, the proto compiles, the meter exists.

---

## Phase 3: User Story 1 - Data changes are audited atomically with the business change (Priority: P1) 🎯 MVP

**Goal**: a data change under `TransactionBehavior` goes into that transaction's outbox and reaches the audit
database if and only if the transaction committed (FR-001..006).

**Independent Test**: commit a change under `TransactionBehavior`, throw away the provider before any export, start
again with delivery and a consumer: the `DataAudit` row appears. Roll back: it doesn't.

### Tests for User Story 1 ⚠️ write first, see them fail

- [X] T016 [P] [US1] Add to `test/Juice.Audit.Scopes.Tests/AuditScopeFixture.cs` an option that builds a SQLite `SampleOrderContext` (`DbContextBase`, `IOutboxContext`, an auditable `Order`), its `TransactionBehavior` subclass, `AddOutbox()`, a policy routing `juice.audit` to `"local"`, and a capturing `IAuditService` / collector
- [X] T017 [P] [US1] T-OUTBOX-1 and T-OUTBOX-2 in `test/Juice.Audit.Scopes.Tests/OutboxSinkTests.cs`: a commit gives one outbox row with all N entries, and the access record has `OutboxedChanges = N` and no entries; a rollback gives no outbox row and `UncommittedChanges = N`
- [X] T018 [P] [US1] T-OUTBOX-3 in `test/Juice.Audit.Scopes.Tests/OutboxSinkTests.cs`: a handler that changes a second audited entity in dispatch round 2; the saved payload holds the entries of both rounds (guards research R3)
- [X] T019 [P] [US1] T-OUTBOX-4 in `test/Juice.Audit.Scopes.Tests/OutboxSinkTests.cs`: each fallback reason (no transaction, no binding, no route, tenant mismatch, sink exception) sends the change best effort with `BestEffortChanges` and increments `juice.audit.outbox.entries{outcome=best_effort,reason=…}` (use a `MeterListener`)
- [X] T020 [P] [US1] T-OUTBOX-5 in `test/Juice.Audit.Scopes.Tests/OutboxSinkTests.cs`: over `MaxEntriesPerEvent` the entries are split into parts; an entry over `MaxEventBytes` has `Changes` truncated with `$truncated` and `$originalSize`
- [X] T021 [P] [US1] T-OUTBOX-6 in `test/Juice.Audit.Scopes.Tests/OutboxSinkTests.cs`: the same `DataChangesAudited` handled twice stores each entry once
- [X] T022 [P] [US1] The SC-001 crash test in `test/Juice.Audit.Scopes.Tests/OutboxCrashTests.cs`: commit, dispose the provider before `TransactionCommitted` handling and before any export, then a new provider with `AddDeliveryProcessor<SampleOrderContext>("local")` and `SubscribeAuditDataChanges()` delivers the row; run 1 000 iterations behind `IgnoreOnCIFact`, 20 otherwise

### Implementation for User Story 1

- [X] T023 [P] [US1] Create `IDataChangeSink` (`bool TryAppend(Guid transactionId, DataAudit entry)`, never throws) in `src/Juice.Audit/IDataChangeSink.cs` (contracts/public-api.md)
- [X] T024 [US1] Extend `src/Juice.Audit/AuditContext.cs` as in data-model.md: `AddOutboxedPending(txId)`, `OutboxedChangeCount`, `BestEffortChangeCount`; commit moves pending → `OutboxedChangeCount`; `IsRequestedForAuditLog` counts pending and outboxed; `UncommittedAuditEntryCount` includes outboxed pending that never committed
- [X] T025 [US1] Change `src/Juice.Audit.Api/NotificationHandlers/DataEvenNotificationtHandler.cs`: build the entry; if `TransactionId` is set and `IDataChangeSink` (optional service) takes it → `AddOutboxedPending`; otherwise today's path and mark it best effort. Without a scope, still offer it to the sink (trace id from `Activity.Current`, tenant from the event or `ITenantAccessor`) unless `RequireScope` (research R5)
- [X] T026 [US1] Change `src/Juice.Audit.Api/NotificationHandlers/TransactionCommittedHandler.cs` to commit both the uncommitted entries and the outboxed pending counts
- [X] T027 [US1] Write `OutboxedChanges` and `BestEffortChanges` metadata in `AuditRecorder.PersistAsync` in `src/Juice.Audit.Api/Scopes/AuditRecorder.cs`, next to `UncommittedChanges`
- [X] T028 [P] [US1] Create `DataChangesAudited` (`[Domain("juice.audit")]`, fields as in data-model.md, only strings and primitives) and `DataChangeEntry` in `src/Juice.Audit.Messaging/DataChangesAudited.cs`
- [X] T029 [P] [US1] Create `AuditOutboxOptions` (`AppName`, `MaxEntriesPerEvent = 500`, `MaxEventBytes = 524288`, `RequireScope = false`) in `src/Juice.Audit.Messaging/Outbox/AuditOutboxOptions.cs`
- [X] T030 [US1] Create `AuditOutboxBinding<TContext>` in `src/Juice.Audit.Messaging/Outbox/AuditOutboxBinding.cs`: `bool Owns(Guid txId)` resolves the scoped `TContext` and compares `Database.CurrentTransaction?.TransactionId`; it exposes `IOutboxService<TContext>` (research R2)
- [X] T031 [US1] Create `OutboxDataChangeSink` in `src/Juice.Audit.Messaging/Outbox/OutboxDataChangeSink.cs`: find the owning binding; check the route with `IMessagePublishingPolicy` (cached per tenant for the scope; ignore `local-channel`); check that the tenant matches the scope; one open event per (tx, tenant), created with `AddEventAsync` and appended to afterwards; split and truncate at the limits; lock; catch everything and return false; record `juice.audit.outbox.entries` (research R3, R4, R6) (depends on T028–T030)
- [X] T032 [US1] Create `DataChangesAuditedHandler` (`IIntegrationEventHandler<DataChangesAudited>` → `SaveAuditBatchCommand` with data-only items; throws on `Retryable` so the broker retries; rejects invalid entries one at a time) in `src/Juice.Audit.Messaging/Collector/DataChangesAuditedHandler.cs`
- [X] T033 [US1] Make `src/Juice.Audit.Messaging/MessageAuditBehavior.cs` pass `DataChangesAudited` straight to `next` (research R11)
- [X] T034 [US1] Add `AddAuditOutbox<TContext>(Action<AuditOutboxOptions>?)` (registers the binding and the sink with `TryAdd`, and a startup log for audited contexts without a binding) and `IConsumerBuilder.SubscribeAuditDataChanges()` in `src/Juice.Audit.Messaging/DependencyInjection/`
- [X] T035 [US1] Update `test/Juice.Audit.Scopes.Tests/TransactionAuditTests.cs` so the existing cases run both without a sink (unchanged results) and with one (entries outboxed, counted)

**Checkpoint**: committed changes reach the collector through the outbox, rollbacks leave nothing, and every
fallback is visible.

---

## Phase 4: User Story 2 - Auditing no longer slows down or blocks requests (Priority: P1)

**Goal**: finishing a scope only enqueues. A background exporter sends batches with a deadline, retries and a flush
on shutdown (FR-007..013, FR-024).

**Independent Test**: with a collector that hangs or is stopped, an audited endpoint keeps its latency and succeeds.
When the collector returns, the records arrive once.

### Tests for User Story 2 ⚠️ write first, see them fail

- [X] T036 [P] [US2] T-EXPORT-1 in `test/Juice.Audit.Scopes.Tests/ExportQueueTests.cs`: with an exporter that never completes, `PersistAuditInformationAsync` returns in under 1 ms and the scope completes
- [X] T037 [P] [US2] Batching tests in `test/Juice.Audit.Scopes.Tests/ExportQueueTests.cs`: a batch goes when it reaches `MaxBatchSize` or `MaxBatchAge`; items are never split; retries back off with jitter; `Retryable` records are retried and `Rejected` ones are not; a flush on `StopAsync` within `ShutdownFlushTimeout`; `Synchronous = true` exports inline (use a fake `TimeProvider` where needed)
- [X] T038 [P] [US2] T-EXPORT-3 and a deadline test in `test/Juice.Audit.Tests/GrpcBatchTests.cs` against an in-memory `TestServer` gRPC host: `LogBatch` round trip with ids; `Unimplemented` → unary fallback with nothing dropped and one warning; a hanging server is cancelled at `ExportTimeout` through `LogBatch` and through the unary fallback (one deadline for the batch, unsent records `Retryable`)

### Implementation for User Story 2

- [X] T039 [P] [US2] Create `AuditExportOptions` (research R9 defaults, bound from `Audit:Export`) in `src/Juice.Audit/Export/AuditExportOptions.cs` and `AuditExportItem` in `src/Juice.Audit/Export/AuditExportItem.cs`
- [X] T040 [P] [US2] Create `IAuditExporter` and `AuditExportResult` (per-record outcomes) in `src/Juice.Audit/Export/IAuditExporter.cs`
- [X] T041 [US2] Create `AuditExportQueue` (singleton; `Channel.CreateBounded`, `FullMode = DropWrite`; `TryEnqueue` records `dropped{reason=queue_full}`; a queue size gauge) in `src/Juice.Audit/Export/AuditExportQueue.cs` (depends on T014, T039)
- [X] T042 [US2] Create `AuditExportWorker : BackgroundService` in `src/Juice.Audit/Export/AuditExportWorker.cs`: read up to `MaxBatchSize` or until `MaxBatchAge`; call the exporter (resolved per batch in a new scope) with the worker's stop token only, since each exporter applies `ExportTimeout` itself (T044, T045); retry with backoff of 1 s × 2ⁿ ± 20%, capped at 30 s, up to `MaxRetryAttempts`, then `dropped{reason=retries_exhausted}`; on stop, flush until `ShutdownFlushTimeout`, then `dropped{reason=shutdown}`; never throws out of `ExecuteAsync` (depends on T040, T041)
- [X] T043 [US2] Create `QueuedAuditService : IAuditService` in `src/Juice.Audit/Services/QueuedAuditService.cs`: enqueue the access record with its best-effort entries; `Synchronous` → call the exporter inline and return its result; when the queue is full, return success, because the drop is already counted by `AuditExportQueue` (`dropped{reason=queue_full}`, T041) and logged at a limited rate by `AuditDropReporter` (T051). A `Failed` here would make `AuditRecorder` warn once per dropped record (`AuditRecorder.cs:82`), against FR-027
- [X] T044 [P] [US2] Create `InProcessAuditExporter` (new scope → `IMediator.Send(SaveAuditBatchCommand)` under a `CancellationTokenSource(ExportTimeout)` linked to the caller's token) in `src/Juice.Audit/Services/InProcessAuditExporter.cs`
- [X] T045 [US2] Create `GrpcAuditExporter` in `src/Juice.Audit/Services/GrpcAuditExporter.cs`: the exporter owns the deadline on every path: `LogBatch` with `deadline = now + ExportTimeout`; map results; on `Unimplemented`, switch for the process lifetime to unary `Log` using `AuditMessageMapper`, with one warning naming collector 10.7 (research R10). In fallback, every unary call of a batch shares that batch's single deadline (`CallOptions(deadline:)` plus the caller's token); records not sent before it are `Retryable`. Never reuse `GrpcAuditService` as is, because it has no deadline (constitution VI) (depends on T013)
- [X] T046 [US2] Implement `LogBatch` in `src/Juice.Audit.Api/Grpc.Services/AuditGrpcServer.cs` (map → `SaveAuditBatchCommand` as the root tenant → `AuditBatchResult`; a database outage gives status `Unavailable`), and switch `Log` to the shared mapper
- [X] T047 [US2] Wire the modes in `src/Juice.Audit/DependencyInjection/AuditServiceCollectionExtensions.cs` and `src/Juice.Audit.Api/Extensions/ApiAuditServiceCollectionExtensions.cs`: `AddAuditExport()` (options, queue, worker, metrics, `QueuedAuditService`); `ConfigureAuditGrpcClient` → `GrpcAuditExporter`; `ConfigureAuditDefault` → `InProcessAuditExporter`; keep the signatures (FR-023)
- [X] T048 [US2] Set `Synchronous = true` in the test setups that read the database right after a request, in `test/Juice.Audit.Tests/*` (via `Juice.Audit.Tests.Host` configuration `Audit:Export:Synchronous`), so existing tests keep their assertions (SC-007)

**Checkpoint**: requests never wait for audit; the exporter survives collector outages; both P1 stories work
independently.

---

## Phase 5: User Story 3 - Operators can see audit loss and backlog (Priority: P2)

**Goal**: every drop is counted and logged at a limited rate; the collector reports its throughput (FR-025..027).

**Independent Test**: fill the queue with the collector stopped. The dropped metric equals the records refused, and
there's one warning per interval with the count.

### Tests for User Story 3 ⚠️

- [X] T049 [P] [US3] T-EXPORT-2 in `test/Juice.Audit.Scopes.Tests/ExportMetricsTests.cs` with a `MeterListener`: `dropped{reason=queue_full}` equals the refused records; `exported`, `retries` and `batches.failed` match a scripted exporter; the queue size gauge follows the queue
- [X] T050 [P] [US3] Warning rate test in `test/Juice.Audit.Scopes.Tests/ExportMetricsTests.cs`: 10 000 drops in one interval give one warning, with the count and reasons, and `AuditRecorder` logs no warning of its own for them (capture the logger through a full scope)

### Implementation for User Story 3

- [X] T051 [US3] Create `AuditDropReporter` in `src/Juice.Audit/Export/AuditDropReporter.cs`: counts drops by reason and logs `Dropped {Count} audit records in the last {Interval} ({Reasons}).` at most every 30 s (configurable); called by the queue and the worker (FR-027)
- [X] T052 [P] [US3] Create `AuditCollectorMetrics` (`juice.audit.collector.records`, `write.duration`, `batch.size` with the `kind` tag) in `src/Juice.Audit.Api/Collector/AuditCollectorMetrics.cs`; record from `SaveAuditBatchCommandHandler` via an optional `IAuditBatchObserver` defined in `src/Juice.Audit/Domain/` so `Juice.Audit` doesn't reference `Juice.Audit.Api`
- [X] T053 [US3] Add `juice.audit.export.duration` and `export.queue.capacity` recording in `AuditExportWorker` and `AuditExportQueue` if not done already, and check every instrument in contracts/metrics.md has a recording call site

**Checkpoint**: loss and backlog are visible in OpenTelemetry metrics, in the app and in the collector.

---

## Phase 6: User Story 4 - The collector writes in batches and owns the audit database (Priority: P2)

**Goal**: one writer, bulk and idempotent writes, tenant-correct, no self-auditing (FR-014..017, FR-019..021).

**Independent Test**: an app in remote mode with no audit connection string. Its records appear in the collector's
query API; a replayed batch adds no rows.

### Tests for User Story 4 ⚠️

- [X] T054 [P] [US4] `test/Juice.Audit.Tests/IdempotentWriteTests.cs` (`IgnoreOnCIFact`, PostgreSQL and SQL Server from `ConnectionStrings__PostgreConnection` / `SqlServerConnection`): a replayed batch leaves one copy; two concurrent writers of the same batch leave one copy with no error (unique violation → record-by-record path); a 512-record batch takes at most ⌈512 / MaxBatchSize⌉ + 1 commands per table (counted with a `DbCommandInterceptor`), with the collector's `AuditDbContext` set to `MaxBatchSize(1000)` on SQL Server (SC-003)
- [X] T055 [P] [US4] Mixed-tenant test in `test/Juice.Audit.Tests/MultiTenantAuditTests.cs`: a `LogBatch` with records of tenants A and B; each tenant reads only its own and the root reads both
- [X] T056 [P] [US4] Self-audit test in `test/Juice.Audit.Scopes.Tests/CollectorTests.cs`: with `AddMessageAudit()` and `UseAudit()` on the collector, handling `DataChangesAudited` and serving `LogBatch` produce no access records

### Implementation for User Story 4

- [X] T057 [US4] Add the unique violation fallback to `SaveAuditBatchCommandHandler` in `src/Juice.Audit/Domain/CommandHandlers/SaveAuditBatchCommandHandler.cs`: on an error classified as `UniqueViolation` by `IAuditWriteErrorClassifier` (T009a), call `ClearTracked()` on the repositories and retry one record at a time, counting conflicts as `Duplicate`
- [X] T058 [US4] Add the default exclusion of `/juice.audit.grpc.AuditGrpcService/` to the `AuditFilterOptions` defaults in `src/Juice.Audit.Api/Middleware/` (research R11)
- [X] T059 [US4] Wire the collector in `test/Juice.Audit.Tests.GrpcHost/Program.cs` as in quickstart.md: `ConfigureAuditGrpcHost`, `AddAuditCollector` (empty until US5, or `AddAuditCollector` added there), idempotency store, broker consumer with `SubscribeAuditDataChanges()`, `MapAuditGrpcServer`, `MapJuiceAudit`; `MaxBatchSize(1000)` on the SQL Server `AuditDbContext` options, so SQL Server batches like PostgreSQL instead of 42 commands per round trip (SC-003)
- [X] T060 [US4] Switch `test/Juice.Audit.Tests.Host/Program.cs` to remote mode behind a configuration switch (`Audit:Mode = Remote | InProcess`) with no `AuditDbContext` registration in remote mode (SC-006); T070 later replaces this switch with `AddAuditPipeline`

**Checkpoint**: the collector is the only writer; replays and concurrent writers are safe.

---

## Phase 7: User Story 5 - Records are processed in the collector before storage (Priority: P3)

**Goal**: ordered processors with built-in redaction and truncation; a failure affects one record (FR-018).

**Independent Test**: a record with `Authorization` and `Cookie` headers and a 5 MB body is stored with the headers
masked, the body truncated, and the metadata noting both.

### Tests for User Story 5 ⚠️

- [X] T061 [P] [US5] `test/Juice.Audit.Scopes.Tests/CollectorProcessorTests.cs`: redaction of the configured headers (case-insensitive, in the request and response header JSON); truncation of `Req_Data`, `Res_Data`, `Res_Err` and `DataAudit.Changes` with `Truncated` metadata; processors run in `Order`; one that throws or rejects affects only its record, and the others are stored

### Implementation for User Story 5

- [X] T062 [P] [US5] Create `IAuditRecordProcessor`, `AuditRecordEnvelope` and `ProcessorResult` in `src/Juice.Audit/Domain/Processing/` (the abstractions sit in `Juice.Audit` so the batch handler can run them; contracts/public-api.md lists them under `Juice.Audit.Collector`, so use that namespace)
- [X] T063 [P] [US5] Create `AuditCollectorOptions` (`RedactHeaders` default list, `RedactMask`, `MaxBodyLength = 65536`, bound from `Audit:Collector`) in `src/Juice.Audit.Api/Collector/AuditCollectorOptions.cs`
- [X] T064 [P] [US5] Create `HeaderRedactionProcessor` (Order 100; metadata `Redacted`) in `src/Juice.Audit.Api/Collector/HeaderRedactionProcessor.cs`
- [X] T065 [P] [US5] Create `TruncationProcessor` (Order 200; metadata `Truncated` with the original sizes) in `src/Juice.Audit.Api/Collector/TruncationProcessor.cs`
- [X] T066 [US5] Run the processors in `SaveAuditBatchCommandHandler` (after deduplication, before `AddRange`; catch per record → `Rejected`)
- [X] T067 [US5] Add `AddAuditCollector(Action<AuditCollectorOptions>?)` returning a builder with `AddProcessor<T>()`, registering both built-ins and the collector metrics, in `src/Juice.Audit.Api/Extensions/ApiAuditServiceCollectionExtensions.cs`

**Checkpoint**: secrets and oversized bodies are handled centrally.

---

## Phase 8: User Story 6 - A single app can run the pipeline in-process (Priority: P3)

**Goal**: `AddAuditPipeline` in `InProcess` mode (the in-process exporter plus the local transport) gives the same guarantees with no broker and no separate
service (FR-022).

**Independent Test**: an app in in-process mode passes the acceptance scenarios of US1 and US2.

### Tests for User Story 6 ⚠️

- [X] T068 [P] [US6] `test/Juice.Audit.Tests/InProcessModeTests.cs` (`IgnoreOnCIFact`, real audit database): through `Juice.Audit.Tests.Host` in `InProcess` mode, a committed change and its access record both reach `AuditDbContext`; a rollback leaves no `DataAudit` row; requests don't wait on the database (measure with a slowed `SaveChanges` interceptor); plus a registration test: no `Audit:Mode` and an unknown value both throw at startup with the key in the message

### Implementation for User Story 6

- [X] T069 [US6] Add `SampleOrderContext` (`DbContextBase`, `IOutboxContext`, an auditable `Order`), its `TransactionBehavior` subclass, a `PlaceOrder` command and an endpoint to `test/Juice.Audit.Tests.Host/` (new folder `Sample/`), with migrations or `EnsureCreated` for the test database
- [X] T070a [US6] Add `MessagingBuilder.AddAuditPipeline<TContext>(IConfiguration, Action<AuditPipelineOptions>?)` and `AuditPipelineOptions` (`ConfigureDb: Action<DbOptions>?`, `ConfigureGrpcClient: Action<GrpcClientFactoryOptions>?`, `ConfigureOutbox: Action<AuditOutboxOptions>?`) in `src/Juice.Audit.Messaging/DependencyInjection/AuditPipelineExtensions.cs` (contracts/public-api.md "Choosing the mode"): read `Audit:Mode` (required, see below); InProcess → `ConfigureAuditDefault(configuration, ConfigureDb)`, `AddLocalPublisher`, `AddLocalConsumer(c => c.SubscribeAuditDataChanges())`, `AddDeliveryProcessor<TContext>("local")`; Remote → `ConfigureAuditGrpcClient` with the address from `Audit:Grpc:Address`, then `ConfigureGrpcClient` (which can override it); both → `AddAuditOutbox<TContext>(ConfigureOutbox)`; Remote with no address from either source fails at startup with a clear message (FR-022, constitution I). `Audit:Mode` has no default: a missing or unknown value throws `InvalidOperationException` at registration naming the key, the valid values and the section to set, so a remote app can't fall into in-process mode by a missing setting. First check that core's `AddDelivery` can be called both by the app and by this method; if it can't, register the delivery processor only when none is registered for `TContext` (depends on T034, T047)
- [X] T070 [US6] Replace the mode wiring in `test/Juice.Audit.Tests.Host/Program.cs`, including T060's switch, with one `AddAuditPipeline<SampleOrderContext>(builder.Configuration)` call inside `AddMessaging`, plus `AddOutbox()` and a `juice.audit` → `local` policy in `appsettings.json` for `InProcess` (depends on T069, T070a)

**Checkpoint**: every story works on its own and together.

---

## Phase 9: Polish & Cross-Cutting Concerns

- [X] T071 [P] README.MD: a new "Delivery guarantees" section (the spec's table); remote and in-process setup; `Audit:Export` and `Audit:Collector` options; `AddAuditOutbox<TContext>()` and the `juice.audit` route; the metrics table (contracts/metrics.md); the no-scope behavior change and `RequireScope` (research R5); the upgrade order (collector first, plan.md Rollout)
- [X] T072 [P] XML docs on every new public type and option, in the style of the existing code (what it does, when to use it)
- [X] T073 Build and test on every target framework: `dotnet build Juice.Audit.sln` and `dotnet test` for `test/Juice.Audit.Scopes.Tests` and `test/Juice.Audit.Tests` on net8.0, net9.0 and net10.0; the net6.0 build of the libraries must pass (`AuditId` fallback, `Channel`)
- [X] T074 Run quickstart.md sections 1–3 by hand against the test hosts and record the results in the PR (remote mode, 2026-11-24: see "End-to-end run" below)
- [ ] T075 [P] Note the core follow-ups from plan.md (`AuditEvent` context type or a current-outbox accessor; the outbox after an execution strategy replay) as issues in the Juice core repository, with links in the PR

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (1)**: none.
- **Foundational (2)**: after Setup; **blocks every story**.
- **US1 (3)** and **US2 (4)**: after Foundational. Independent of each other: US1 doesn't need the exporter (its
  fixture captures `IAuditService`), and US2 doesn't need the outbox.
- **US3 (5)**: after US2 (it reports on the queue and the worker). The outbox counter it checks is recorded by US1,
  so T049's outbox assertions wait for US1.
- **US4 (6)**: after Foundational; T059 needs `SubscribeAuditDataChanges` (US1, T034) and `LogBatch` (US2, T046).
- **US5 (7)**: after Foundational (T010 hook point). Independent of the other stories.
- **US6 (8)**: after US1 and US2.
- **Polish (9)**: after the stories that are shipped.

### Story completion order

```text
Setup → Foundational ─┬─► US1 ─┬────────────► US6
                      ├─► US2 ─┼─► US3        │
                      │        └─► US4 ◄─ US1 │
                      └─► US5                 ▼
                                           Polish
```

### Within each story

Tests first and failing → types and options → services → wiring (DI and hosts) → the existing tests updated.

## Parallel Examples

**Foundational**: T005, T006, T008, T009a, T012 and T014 touch different files; run them together, then T007, T009 → T010 →
T011, and T013 after T012.

**US1**: the tests T016–T022 together; then T023, T028 and T029 together; T024 → T025 → T026 → T027 in sequence (the
same flow through `AuditContext`); T030 → T031 → T034; T032 and T033 in parallel with T031.

**US2**: the tests T036–T038 together; T039, T040 and T044 together; T041 → T042 → T043; T045 and T046 in parallel
after T013; T047 last; then T048.

**US5**: T062–T065 together, then T066 → T067.

## Implementation Strategy

### MVP

1. Phases 1 and 2.
2. **US1** (the guarantee the feature exists for) and **US2** (requests no longer wait for audit). Both are P1; do
   US1 first if compliance is the driver, US2 first if latency or outages are.
3. Stop and validate: T022 (crash test) and T036 (non-blocking), then release `10.7.0-alpha` on `release/10`.

### Incremental delivery

1. MVP (US1 + US2) → alpha.
2. US3 (visibility) and US4 (the collector owns the database) → alpha. This is the minimum before turning on remote
   mode in production.
3. US5 (processors) and US6 (in-process mode) → release candidate.
4. Polish → release `api-10.7.0`.

## Notes

- 77 tasks: Setup 4, Foundational 12, US1 20, US2 13, US3 5, US4 7, US5 7, US6 4, Polish 5.
- Provider-specific error codes live only in `Juice.Audit.EF` (`AuditWriteErrorClassifier`, T009a); `Juice.Audit`
  stays free of EF and of the providers.
- Commit after each task or logical group; stop at any checkpoint to validate a story on its own.

## Implementation notes (MVP, phases 1–4, 2026-11-20)

Where the code differs from the task text, and why:

- **T008**: the batch operations are on a new `IAuditBatchStore` (`src/Juice.Audit/Domain/`, implemented by
  `Juice.Audit.EF/AuditBatchStore.cs`), not on `IAccessLogRepository` / `IDataAuditRepository`. Adding members to those
  public interfaces would break every implementation (the test host has two), a breaking change in a minor version.
  `SaveAuditInfoCommandHandler` uses the store when one is registered, the repositories otherwise.
- **T010**: the processor hook point is left to T066 (US5); nothing calls it before then.
- **T002**: `IOutboxService<TContext>` is in `Juice.Messaging`, already referenced; only `Juice.Messaging.Local` was
  added (it brings `IConsumerBuilder` through `Juice.EventBus`).
- **T016**: the outbox fixture is its own file, `OutboxFixture.cs`, not part of `AuditScopeFixture`.
- **T022**: in `Juice.Audit.Scopes.Tests`, 100 runs (50 commits, 50 rollbacks) on every build. Delivery is simulated by
  reading the outbox table; the delivery processor is Juice core's and tested there.
- **T023**: `IDataChangeSink.TryAppendAsync(Guid? transactionId, …)` is async, because
  `IMessagePublishingPolicy.ResolveAsync` is, and takes a nullable id so the sink counts `no_transaction` too.
- **T026**: no change to `TransactionCommittedHandler`; `AuditContext.CommitAuditEntries` now settles both kinds.
- **T028**: no `Final` field on `DataChangesAudited` (it could only be guessed at save time).
- **T034**: no startup log of contexts without a binding (nothing lists the audited contexts); the first unclaimed
  transaction logs one warning per process instead, and every one is counted (`reason=no_binding`).
- **T040**: `IAuditExporter` returns `AuditBatchResult` (the batch command's result) instead of a separate
  `AuditExportResult`.
- **T041**: the channel uses `FullMode = Wait` with `TryWrite`, not `DropWrite`: `DropWrite` makes `TryWrite` report
  success while it drops, so the drop couldn't be counted. Nothing awaits `WriteAsync`, so no caller waits.
- **T042**: on .NET 10, `BackgroundService` starts `ExecuteAsync` with `Task.Run` on the stopping token; a stop right
  after the start would skip it and lose the queue uncounted. `StopAsync` flushes in that case.
- **T048**: set in `Juice.Audit.Tests.Host/appsettings.json`. The 14 database tests could not run on the machine that
  built this (no connection strings); they fail the same way without these changes.
- **Found on the way**: `GrpcAuditService` never sent the access record's metadata (a `JsonTextWriter` that was neither
  flushed nor rewound). The shared `AuditMessageMapper` fixes it for every gRPC path.

## Implementation notes (phases 5–9, 2026-11-24)

- **T051**: `AuditDropReporter` writes the warning when the interval that the first drop opened ends, or at once on
  shutdown (`Flush`), so a burst gives one warning with its total. The worker's own per-batch drop warnings became
  debug logs. New option `Audit:Export:DropWarningInterval` (30 s).
- **T052**: `AuditCollectorMetrics` is in namespace `Juice.Audit.Collector` (with the processors), registered by
  `AddAuditCollectorMetrics()`, which `ConfigureAuditGrpcHost`, `ConfigureAuditDefault` and `AddAuditCollector` call.
- **T054**: SQL Server's default of 42 rows per command is raised to `MaxBatchSize(1000)` in `AddAuditDbContext`
  itself (no hook exists to set it from a host); EF still splits at the 2100 parameter limit, so a 512-record batch of
  access logs takes about 8 commands there, and 1 on PostgreSQL. The test bounds each provider accordingly.
- **T055, T056**: in `GrpcBatchTests.cs`, which has the in-memory gRPC host. The message side of T056 was already
  covered by `OutboxSinkTests` (R11).
- **T057**: already in `AuditBatchWriter` since phase 2 (a non-transient save failure goes record by record, a unique
  violation is a duplicate).
- **T059**: the RabbitMQ consumer is added only when `Audit:RabbitMQ:Host` is set, so the host runs without a broker.
  The read API is mapped `AllowAnonymous()` there: a test host has no sign-in.
- **T060**: replaced by T070's `AddAuditPipeline` (`Audit:Mode` in `appsettings.json`).
- **T062**: records are changed through new public methods `RequestInfo.Rewrite`, `ResponseInfo.Rewrite` and
  `DataAudit.RewriteChanges`, so the app's own processors can use them too. The single-record `Log` path runs the
  processors as well.
- **T069**: the sample orders use an in-memory SQLite database, with `DateTimeOffset` stored as a number: Juice's
  outbox delivery queries order and compare `DateTimeOffset`, which SQLite can't. The host now calls
  `UseMessageContext()`, which `TransactionBehavior` needs.
- **T070a**: core's `AddDelivery` can be called twice, but two `AddDeliveryProcessor<TContext>("local")` would run two
  delivery loops, and the registration can't be inspected (a factory). `AuditPipelineOptions.AddLocalDeliveryProcessor`
  (default `true`) lets an app that already registers it opt out.
- **T073**: all tests pass on net8.0, net9.0 and net10.0 against PostgreSQL 16 and SQL Server 2022 containers (90 and
  64 tests); the libraries build on net6.0.
- **Found on the way (core)**: `LocalTransportPublisher` first deserializes by `$type`, and the serializer's allow-list
  rejects `List<DataChangeEntry>` (it checks `System.Private.CoreLib`, the list's assembly, not the element type). It
  logs one warning per process and falls back to the `x-message-clr-type` header, which works. A core follow-up, with
  T075's.

### End-to-end run (T074, remote mode, 2026-11-24)

`Juice.Audit.Tests.Host` (Audit:Mode=Remote) → gRPC `LogBatch` and outbox → RabbitMQ 4.1 → `Juice.Audit.Tests.GrpcHost`
→ PostgreSQL 16. The hosts gained what remote mode needs when `Audit:RabbitMQ:Host` is set: the app's RabbitMQ producer
and delivery processor, the `juice.audit` exchange and queue (`InitRabbitMQInfrastructureAsync`), and the collector's
database migration.

| Check | Result |
|---|---|
| 1–2. An order, then the trace on the collector | 0.27 s cold; access record and data change under one trace id, `OutboxedChanges: 1` |
| 100 orders, collector up | access 100/100, data 100/100, each once; the last data changes ~14 s behind (delivery polling) |
| 3. Collector stopped, 100 orders, restarted at ~30 s | every order 200, slowest 21 ms; ~10 s after the restart access 100/100 and data 100/100, once each; no drop |
| 4. RabbitMQ stopped, 20 orders, restarted at ~25 s | orders 200; access records arrive over gRPC at once, data 0; ~20 s after the restart data 20/20 |
| Collector down past the retries (~31 s) | the 10 access records dropped, one warning `Dropped 10 audit records in the last 00:00:31 (retries_exhausted: 10)`; their data changes waited in RabbitMQ and arrived on restart |

Totals after the run: 201 access records and 201 data changes, all distinct ids.

### Second review (2026-11-24)

- **Rolled-back transactions (core follow-up, with T075's)**: `TransactionBehavior`'s `catch` clears neither the
  context's tracked changes and `PendingAuditEntries` nor the outbox's pending messages. The scope's next transaction
  then saves the failed one's events and replays its changes: rows of the failed command are inserted, and audited as
  that transaction's. Audit side: a `DataChangesAudited` written while another transaction is the context's
  (`ShouldSerializeEntries`) leaves its entries out, so the outbox part is covered (also for an execution-strategy
  replay); the replayed changes are core's to fix.
- **App-opened transactions**: core gives a change a transaction id only under `TransactionBehavior`
  (`IsManaged`), so they already go best effort (`no_transaction`); the sink also checks `IsManaged` (`unmanaged`).
