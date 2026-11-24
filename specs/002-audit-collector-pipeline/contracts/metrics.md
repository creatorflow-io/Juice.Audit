# Contract: Metrics (`System.Diagnostics.Metrics`, meter `Juice.Audit`)

The metric names follow OpenTelemetry naming conventions. Apps collect them with
`.WithMetrics(m => m.AddMeter("Juice.Audit"))`.

## App (exporter), FR-025

| Instrument | Type | Unit | Tags |
|---|---|---|---|
| `juice.audit.export.queue.size` | ObservableGauge | `{record}` | — |
| `juice.audit.export.queue.capacity` | ObservableGauge | `{record}` | — |
| `juice.audit.export.records` | Counter | `{record}` | `outcome` = `exported` \| `duplicate` \| `dropped`; `reason` (only when dropped) = `queue_full` \| `retries_exhausted` \| `shutdown` \| `rejected` |
| `juice.audit.export.retries` | Counter | `{batch}` | — |
| `juice.audit.export.batches.failed` | Counter | `{batch}` | `error` = the gRPC status or the exception type |
| `juice.audit.export.duration` | Histogram | `s` | `outcome` = `success` \| `failure` |
| `juice.audit.outbox.entries` | Counter | `{record}` | `outcome` = `outboxed` \| `best_effort`; `reason` (only for best effort) = `no_transaction` \| `no_binding` \| `no_route` \| `tenant_mismatch` \| `error` |

The drop warning (FR-027) is logged at most once every 30 s:
`Dropped {Count} audit records in the last {Interval} ({Reasons}).`

## Collector, FR-026

| Instrument | Type | Unit | Tags |
|---|---|---|---|
| `juice.audit.collector.records` | Counter | `{record}` | `kind` = `access` \| `data`; `outcome` = `received` \| `stored` \| `duplicate` \| `rejected` \| `failed` |
| `juice.audit.collector.write.duration` | Histogram | `s` | `kind` |
| `juice.audit.collector.batch.size` | Histogram | `{record}` | `kind` |
