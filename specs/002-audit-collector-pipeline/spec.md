# Feature Specification: Audit Collector Pipeline (app → collector → db)

**Feature Branch**: `002-audit-collector-pipeline`  
**Created**: 2026-11-19  
**Status**: Draft  
**Input**: User description: "the audit service should work like the OTel model: app -> collector -> db. Outbox for data changes, in-memory for access logs."

## Context

Today an audit record is saved inside the request it describes. `AuditMiddleware` (and `AuditScopeRunner` for messages
and mediator requests) awaits `AuditRecorder.PersistAsync` in its `finally` block, which calls `IAuditService` in one of
two ways:

- `ConfigureAuditDefault()`: the app writes to the audit database itself, so every app carries the audit
  `DbContext`, its migrations and its connection string.
- `ConfigureAuditGrpcClient()`: one unary gRPC call per record, with no deadline, no retry and no buffer.

Problems with this:

1. Every audited request waits for the audit write.
2. If the audit server stalls, the requests stall too, because the gRPC call has no deadline.
3. If the audit server or database is down, the record is lost. The only trace left is a warning in the log.
4. Records are written one at a time, so the audit database takes inserts at the full request rate.
5. A data change is recorded after the business transaction commits, in a separate write. A crash between the two
   loses the change from the audit trail, even though the change itself was committed.

This feature splits audit into an OTel-style pipeline. The app records and hands the record off without waiting. A
collector, the audit service, batches the records, processes them and writes them to the database. The two kinds of
record need different guarantees, so they travel differently:

| Record | Transport | Guarantee |
|---|---|---|
| **Data change** (`DataAudit`) | Juice outbox, in the business transaction → broker → collector | Saved if and only if the business change committed (*at-least-once*, deduplicated by id) |
| **Access record** (`AccessLog`) | Bounded in-memory queue → background batch export → collector | Best effort: can be lost if the process crashes or the queue is full, and every loss is counted |

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Data changes are audited atomically with the business change (Priority: P1)

A compliance officer needs every committed change to audited data to be in the audit trail, and no change that was
rolled back. Today a change can commit and then be missing from the trail if the process crashes or the audit
server is unreachable when the request ends.

**Why this priority**: An audit trail with silent gaps can't be trusted for compliance. This is the guarantee the
feature exists to give.

**Independent Test**: Run a command through `TransactionBehavior` that changes an audited entity, then kill the
process right after the commit, before the request completes. Restart. The data change must reach the audit database.

**Acceptance Scenarios**:

1. **Given** a command, run by `TransactionBehavior`, that changes an audited entity, **When** the transaction
   commits, **Then** the data change entries are stored in the outbox in the same database transaction, and the
   collector eventually writes them to the audit database.
2. **Given** the same command, **When** the transaction rolls back, **Then** no data change entry for it reaches the
   outbox or the audit database. The access record still notes the number of dropped changes in its metadata
   (`UncommittedChanges`), as it does today.
3. **Given** the audit service or the broker is down when the transaction commits, **When** it comes back, **Then**
   the pending entries are delivered by the outbox's retry intents with no action from the app.
4. **Given** a transaction that changes N audited rows, **When** it commits, **Then** the outbox holds a single
   integration event carrying all N entries, not N events.
5. **Given** an outbox event that the broker delivers twice, **When** the collector processes both copies, **Then**
   each data change is stored once.

---

### User Story 2 - Auditing no longer slows down or blocks requests (Priority: P1)

An API developer wants audited endpoints to respond as fast as unaudited ones, even when the audit service is slow or
down.

**Why this priority**: Today a stalled audit server can hang every audited request. It's a reliability risk that
gets worse under load.

**Independent Test**: Make the collector take 10 seconds per call, or stop it. The latency of an audited endpoint
must stay within the bound in SC-002, and the requests must still succeed.

**Acceptance Scenarios**:

1. **Given** an audited HTTP request, message or mediator scope, **When** it finishes, **Then** handing off the access
   record is an in-memory enqueue. It never waits on the network or a database.
2. **Given** the collector is unreachable, **When** audited requests run, **Then** they complete normally. Access
   records stay in the queue (up to its capacity) and the exporter retries with backoff.
3. **Given** a collector call that hangs, **When** the export deadline passes, **Then** the exporter cancels the call
   and treats it as a failure to retry. No request thread is involved.
4. **Given** the app shuts down gracefully, **When** the host stops, **Then** the exporter sends what is still in the
   queue, within a configured flush timeout.

---

### User Story 3 - Operators can see audit loss and backlog (Priority: P2)

An operator needs to know when access records are being lost or the export is falling behind, before an auditor
discovers a gap.

**Why this priority**: Access records are best effort by design. That's only acceptable if every loss is visible.

**Independent Test**: Fill the queue past capacity while the collector is stopped. The dropped-records metric must
equal the number of records turned away, and a warning must be logged.

**Acceptance Scenarios**:

1. **Given** the exporter is running, **When** an operator reads the app's OpenTelemetry metrics, **Then** they see
   the queue length, and the counts of records exported, retried, dropped (by reason: queue full, retries
   exhausted, shutdown timeout) and failed batches.
2. **Given** records are dropped, **When** it happens, **Then** a warning is logged, at most once per interval with a
   running count, not once per record.
3. **Given** the collector, **When** an operator reads its metrics, **Then** they see the records received, written and
   deduplicated, the batches that failed, and the write latency. Data change entries and access records are counted
   separately.

---

### User Story 4 - The collector writes in batches and owns the audit database (Priority: P2)

A platform owner wants the audit database to have one writer, the audit service, so that apps don't need the audit
schema or its credentials, and insert load grows with batches, not with requests.

**Why this priority**: This removes the audit database from every app's deployment and lets the database be scaled
or replaced in one place.

**Independent Test**: An app configured with the collector client has no audit connection string and no
`AuditDbContext`, and its records still appear in the audit query API.

**Acceptance Scenarios**:

1. **Given** access records arriving from the in-memory exporters of several apps, **When** the collector receives a
   batch, **Then** it checks the batch's ids in one query per table and inserts the batch with EF batching, so the
   number of round trips depends on the batch size, not on how many requests produced the records.
2. **Given** a retried batch that the collector already wrote, **When** it arrives again, **Then** records with an id
   that is already stored are skipped, not duplicated and not an error.
3. **Given** records for several tenants in one batch, **When** they are written, **Then** each one is stored under its
   own tenant and stays visible only to that tenant (and to the root tenant), as today.
4. **Given** the collector consumes data change events from the broker, **When** its own handlers run, **Then** they
   are not themselves audited: no access record for the collector's consumption, and no recursion.

---

### User Story 5 - Records are processed in the collector before storage (Priority: P3)

A security officer wants secrets and oversized bodies removed in one central place, whatever each app's
configuration says.

**Why this priority**: It's valuable, but the pipeline works without it. This is the processor stage of the OTel
model.

**Independent Test**: Send an access record whose request headers contain `Authorization` and `Cookie`, with a 5 MB
body. The stored record must have those headers masked and the body truncated to the configured limit, and it must
be marked as truncated.

**Acceptance Scenarios**:

1. **Given** configured header names to redact, **When** a record contains them, **Then** their values are replaced
   by a mask before storage.
2. **Given** a configured maximum body size, **When** a request or response body, or a data change payload, is larger,
   **Then** it is truncated and its metadata records the original size.
3. **Given** the processors are configured as an ordered list, **When** a record goes through the collector, **Then**
   they run in that order, and a processor that throws fails only that record, not the batch.

---

### User Story 6 - A single app can run the pipeline in-process (Priority: P3)

A developer running one app, or a test suite, wants the same guarantees without deploying a separate audit service
or a broker.

**Why this priority**: It keeps today's `ConfigureAuditDefault()` deployments working. OTel offers the same thing
when an app exports straight to a backend.

**Independent Test**: An app with the in-process collector, using the Juice local transport and no broker, passes
the acceptance scenarios of Stories 1 and 2.

**Acceptance Scenarios**:

1. **Given** the in-process mode, **When** a transaction commits a data change, **Then** the entry goes through the
   app's outbox and the local transport to an in-process collector, which writes it to the audit database.
2. **Given** the in-process mode, **When** an access record is produced, **Then** it goes through the same in-memory
   queue and batch exporter as in the remote mode, and the exporter calls the collector in-process instead of over
   gRPC.

---

### Edge Cases

- **Data change outside `TransactionBehavior`** (a plain `SaveChanges` with no `TransactionId`): there is no outbox
  transaction to join. The entry goes with its access record through the in-memory path, and its metadata marks it
  `Delivery = BestEffort`. This is documented as a weaker guarantee.
- **Audited `DbContext` without an outbox** (it doesn't implement `IOutboxContext`, or uses a different database
  from the outbox context): same fallback as above. Startup logs once, for each such context, that its data changes
  are audited best effort.
- **Data changes and their access record arrive separately**, or the access record is lost: data changes are stored
  without waiting for their access record. They are linked only by trace id, as the query API (`traces/{traceId}`)
  already does. A data change without an access record is valid.
- **Nested scopes**: a command inside a request still joins the request's scope (unchanged). Its data changes go
  through the outbox of the transaction that made them.
- **Very large data change payloads**: an event over the transport's message size limit is split into several events
  of the same transaction. A single entry that still doesn't fit is truncated and marked, never dropped.
- **Queue full**: the new record is dropped and counted. Records already queued are kept, so the oldest are sent
  first.
- **Exporter retries exhausted**: the batch is dropped and counted under its own reason. The exporter goes on with
  the next batch and keeps the queue moving.
- **Process crash**: queued access records are lost, which is accepted for best effort. Data changes are not lost,
  because they are already in the outbox.
- **Old clients**: apps that still call the unary `Log` RPC keep working against the new collector during migration.
- **Old servers**: a new client against a collector without the batch RPC falls back, for the life of the process,
  to the unary `Log` call, one record per call, still in the background and with a deadline. It logs one warning
  naming the minimum collector version (10.7). Nothing is dropped. An old collector ignores the record id, so
  replays can duplicate access records until the collector is upgraded; the README's upgrade order (collector
  first) avoids this.
- **Time logging** (`SaveTimeAsync`): out of scope here. It keeps its current behavior, see Assumptions.

## Requirements *(mandatory)*

### Functional Requirements

**App side: data changes (outbox)**

- **FR-001**: A data change produced inside a transaction managed by `TransactionBehavior` MUST be written to that
  transaction's outbox as part of the same database transaction as the business change. It MUST NOT be saved by any
  path that runs after the commit.
- **FR-002**: All the audited data changes of one transaction MUST be carried by as few integration events as the
  transport's size limit allows, normally one, named in the `juice.audit` domain (e.g. `DataChangesAudited`) and
  routable by the existing publishing policy.
- **FR-003**: Each data change entry MUST get a stable unique id in the app, when it is created. That id is the
  deduplication key all the way to storage.
- **FR-004**: The event MUST carry, for each entry, everything `DataAudit` stores today (user, time, action,
  database, schema, table, keys, original and current values, trace id, tenant id), plus the app name and the
  transaction id.
- **FR-005**: A rolled-back transaction MUST leave no data change event. The `UncommittedChanges` count on the access
  record MUST keep its current meaning.
- **FR-006**: Data changes that can't use an outbox (see Edge Cases) MUST fall back to the in-memory path and be
  marked best effort. They MUST NOT be skipped.

**App side: access records (in-memory)**

- **FR-007**: Finishing an audit scope MUST only enqueue the access record (with any best-effort data changes) into
  a bounded in-memory queue. It MUST NOT wait on I/O.
- **FR-008**: A background exporter MUST send the queue in batches, when either the batch size or the batch age
  limit is reached, whichever comes first.
- **FR-009**: Every export call MUST have a deadline. A failed or timed-out batch MUST be retried with exponential
  backoff and jitter, up to a configured number of attempts.
- **FR-010**: When the queue is full, the new record MUST be dropped and counted.
- **FR-011**: On graceful shutdown, the exporter MUST flush the queue within a configured timeout and count what it
  couldn't send.
- **FR-012**: Each access record MUST get its id in the app (as FR-003), so that a retried batch can be
  deduplicated.
- **FR-013**: Queue capacity, batch size, batch age, deadline, retry attempts, backoff and flush timeout MUST be
  configurable through options bound from configuration, with defaults documented in the README.

**Collector**

- **FR-014**: The collector MUST accept access records in batches through a new gRPC RPC, and keep the existing unary
  `Log` RPC working for older clients.
- **FR-015**: The collector MUST consume the data change events from the broker through Juice.Messaging, with the
  inbox idempotency check, and write their entries.
- **FR-016**: The collector MUST write each received batch to the database in bulk, and skip records whose id is
  already stored (idempotent insert), on both PostgreSQL and SQL Server.
- **FR-017**: The collector MUST store each record under its own tenant, including when a batch mixes tenants.
- **FR-018**: The collector MUST run an ordered, configurable list of processors on each record before storage. It
  MUST ship header redaction and body or payload truncation. A processor failure fails only its record.
- **FR-019**: The collector MUST answer a batch call with the records it stored and the records it refused, so that
  the client retries only what can be retried.
- **FR-020**: The collector's own message consumption and gRPC calls MUST NOT produce audit records.
- **FR-021**: The query API (`MapJuiceAudit`) MUST keep working on the collector, unchanged.

**Modes and compatibility**

- **FR-022**: There MUST be two modes, *remote* (gRPC exporter, broker) and *in-process* (in-process exporter, Juice
  local transport, audit database in the app), set up by one registration call. The mode MUST be chosen by the
  `Audit:Mode` setting, so switching modes needs no code change in the app. The setting has no default: a missing
  or unknown value MUST fail at startup with a clear message. The broker itself stays part of the
  app's messaging setup.
- **FR-023**: `ConfigureAuditDefault()` and `ConfigureAuditGrpcClient()` MUST keep compiling. They map to the
  in-process and remote modes, and the README states what changed in their guarantees.
- **FR-024**: A *synchronous* export option MUST exist for tests, so that a record is stored before the scope
  completes. It MUST be off by default.

**Observability**

- **FR-025**: The app MUST publish exporter metrics through `System.Diagnostics.Metrics` under a `Juice.Audit` meter:
  queue length, records exported, records dropped (with a `reason` tag), retries and failed batches.
- **FR-026**: The collector MUST publish received, written, deduplicated and failed counts, plus write latency, with a
  `kind` tag (`access`, `data`).
- **FR-027**: Drops MUST be logged as a rate-limited warning that includes the count since the last warning.

### Key Entities

- **Data change entry**: one changed row, as `DataAudit` today, with an id given in the app, plus the app name and
  the transaction id.
- **Data changes event**: an integration event that carries the data change entries of one transaction (or part of
  them, when split). It is stored in the outbox with the business change.
- **Access record**: as `AccessLog` today, with an id given in the app. It's linked to its data changes by trace id
  only.
- **Export batch**: a group of access records (with any best-effort data changes) sent in one exporter call. It
  exists only in the app's memory.
- **Processor**: a step of the collector that changes or rejects one record before storage (redact, truncate,
  enrich).
- **Exporter**: in the app, the component that sends batches to the collector, over gRPC or in-process. In the
  collector, the component that writes batches to storage.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In a crash test (process killed right after commit, 1,000 runs), 100% of the committed data changes
  reach the audit database, and none of the rolled-back ones do.
- **SC-002**: With the collector stopped or answering after 10 s, the p99 latency of an audited endpoint is within
  5% (or 2 ms) of the same endpoint with auditing off.
- **SC-003**: At 1,000 audited requests per second against one collector, audit database inserts happen at the batch
  rate, not the request rate (for a 512-record batch, at most ⌈512 / EF max batch size⌉ + 1 round trips per table),
  and the export queue doesn't grow without bound.
- **SC-004**: When access records are lost, the dropped-records metric equals the number of records lost, in every
  test.
- **SC-005**: Replaying any batch or event twice leaves exactly one copy of each record in the audit database.
- **SC-006**: An app in remote mode deploys with no audit connection string and no audit migrations.
- **SC-007**: All existing audit tests pass, in synchronous export mode where they read the database right after a
  request.

## Assumptions

- The apps that need data changes audited with the guarantee of Story 1 already use the Juice outbox
  (`IOutboxContext`) and a broker, or the local transport in in-process mode. No new transport is added.
- Data change events are delivered at least once and possibly out of order. Ordering between data changes of
  different transactions doesn't matter for an audit trail, because every entry carries its own time.
- Losing access records on a crash or overload is acceptable, as long as every loss is counted (Story 3). An access
  record that must survive a crash is out of scope.
- The id of each audit record is a GUID generated in the app (a sortable, time-ordered GUID is preferred for index
  locality). The unique keys of the existing tables are kept or extended to make inserts idempotent, with
  migrations for PostgreSQL and SQL Server.
- Time logging (`TimeSummaries`, `TimeRecords`) keeps its current inline path. It could move to the same exporter
  later, in a separate spec.
- Exporting records as OpenTelemetry LogRecords to an OTel Collector was considered and rejected. Audit payloads
  (bodies, row values) are too large for OTel attributes, audit data needs its own retention and access rules apart
  from operational telemetry, and storing it would need a custom collector exporter. The architecture follows OTel,
  but the pipeline is the audit service's own.
- This is a minor version of `Juice.Audit.*` (10.7) with no breaking change to the public API. The guarantees
  change, and the README and release notes say how.
