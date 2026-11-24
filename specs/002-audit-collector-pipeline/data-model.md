# Data Model: Audit Collector Pipeline

**Feature**: [spec.md](spec.md) | **Research**: [research.md](research.md)

No table, column or index changes. Both tables keep their `Guid Id` primary key. What changes is **when** the id is
set (in the app, at creation) and the metadata keys some records carry.

## Stored entities (unchanged schema)

### DataAudit (`DataAudit` table)

| Field | Change |
|---|---|
| `Id` | Set in the constructor with `AuditId.New()` (time-ordered GUID). EF keeps an id that is already set. It is the deduplication key (FR-003). |
| `User`, `DateTime`, `Action`, `Db`, `Schema`, `Tbl`, `Kvps`, `Changes`, `TraceId`, `TenantId` | Unchanged. |

**Validation**: the length checks stay as today. If `Changes` is over the event limit, it's truncated in the app
(research R6). The `Changes` JSON then carries `"$truncated": true` and `"$originalSize": n` at its root.

### AccessLog (`AccessLog` table)

| Field | Change |
|---|---|
| `Id` | Set in the constructor with `AuditId.New()` (FR-012). |
| `Metadata` | New keys, below. |

**New metadata keys**:

| Key | Type | When |
|---|---|---|
| `OutboxedChanges` | int | The number of the scope's committed data changes sent through the outbox. They're not on this record; find them by trace id. |
| `BestEffortChanges` | int | The number of data changes carried with this record because the outbox couldn't be used (research R4). |
| `UncommittedChanges` | int | Unchanged meaning: changes of transactions that didn't commit. |
| `Truncated` | object | Set by the collector's truncation processor: `{ "Req_Data": originalSize, … }`. |
| `Redacted` | string[] | Set by the collector's header redaction processor: the names of the masked headers. |

## AuditContext (in memory, per scope)

Today's three sets of entries, plus one:

```
AuditEntries            committed, carried with the access record           (best effort)
_uncommittedEntries     txId → entries, waiting for TransactionCommitted    (best effort)
_outboxedPending        txId → count, taken by the outbox, not yet committed  NEW
OutboxedChangeCount     sum of counts whose txId committed                    NEW
```

State of one data change entry:

```
              AuditEvent (TransactionId?)
                        │
         sink.TryAppend(tx, entry)?
          ┌─────yes─────┴─────no──────┐
          ▼                           ▼
   outboxed-pending(tx)       tx? ──yes──► uncommitted(tx) ──commit──► AuditEntries ──► exporter
          │                    │                 │
   commit │  rollback          no                └─scope ends─► counted in UncommittedChanges
          ▼        ▼           ▼
 OutboxedChangeCount  UncommittedChanges   AuditEntries (no transaction) ──► exporter
 (outbox rows commit) (outbox rows roll back with the transaction)
```

`IsRequestedForAuditLog` = `AuditEntries.Count + UncommittedAuditEntryCount + OutboxedChangeCount + pending > 0`,
so a scope that changed audited data still saves its access record (unchanged rule).

## DataChangesAudited (integration event, outbox payload)

`Juice.Audit.Messaging.DataChangesAudited : IntegrationEvent`, `[Domain("juice.audit")]`, `EventName = "DataChangesAudited"`.

| Field | Type | Notes |
|---|---|---|
| `MessageId`, `CreatedAt` | from `IntegrationEvent` | The inbox key on the collector. |
| `App` | string? | `AuditOutboxOptions.AppName`, falling back to the entry assembly name. |
| `TransactionId` | Guid | The business transaction. |
| `TenantId` | string? | The scope's tenant. It's also the outbox `x-tenant-id` (research R7). |
| `Part`, `Final` | int, bool | The part number when a transaction's entries are split (research R6). `Final` is decided at the outbox save and is informative only. |
| `Entries` | `List<DataChangeEntry>` | Appended until the event is saved. |

`DataChangeEntry`: `Id` (Guid), `User`, `DateTime` (DateTimeOffset), `Action`, `Db`, `Schema`, `Tbl`, `Kvps` (JSON
string), `Changes` (JSON string), `TraceId`, `TenantId`. These are the `DataAudit` fields, one to one.

**Validation on the collector**: an entry with no `Id` or no `Tbl` is rejected and counted. The other entries of the
message are still written.

## Export batch (in memory, app)

```
AuditExportItem  { AccessLog AccessRecord; DataAudit[] BestEffortEntries; DateTimeOffset EnqueuedAt; int Attempts }
```

These sit in `Channel<AuditExportItem>` (bounded, `DropWrite`). The worker reads up to `MaxBatchSize` items, or what
arrives within `MaxBatchAge`, and exports them as one batch. Items are never split across batches.

## Collector record envelope (processor input)

```
AuditRecordEnvelope { Kind: Access | Data; AccessLog? Access; DataAudit? Data; Dictionary<string, object?> Notes }
ProcessorResult     Continue | Reject(reason)
```

Processors run in `Order` ascending and change the envelope in place. A processor that throws or rejects affects only
that record (FR-018).

## Batch result (collector → client)

For each record: `Id`, then `Stored` | `Duplicate` | `Rejected(reason)` | `Retryable(reason)`. The client retries only
`Retryable`. It counts `Rejected` as dropped with `reason = rejected`.
