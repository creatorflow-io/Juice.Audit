# Contract: gRPC (`Juice.Audit.Api.Contracts/Protos/audit.proto`)

This change only adds to the proto, which keeps it compatible with existing clients and servers, as the
constitution's technology constraints require. No field is renumbered or removed.

```proto
message DataAuditMessage {
  // … fields 1-10 unchanged …
  google.protobuf.StringValue id = 11;        // NEW: app-generated Guid ("D" format). Empty = server generates.
}

message AccessLogMessage {
  // … fields 1-26 unchanged …
  google.protobuf.StringValue id = 27;        // NEW: app-generated Guid. Empty = server generates.
}

// NEW
message AuditBatchRequest {
  repeated AuditRequest items = 1;            // each item = one access record + its best-effort data changes
}

// NEW
message AuditRecordResult {
  string id = 1;                              // the record id (access or data)
  Outcome outcome = 2;
  google.protobuf.StringValue reason = 3;

  enum Outcome {
    STORED = 0;
    DUPLICATE = 1;                            // already stored: success for the client
    REJECTED = 2;                             // invalid or refused by a processor: do not retry
    RETRYABLE = 3;                            // transient (database unavailable): retry
  }
}

// NEW
message AuditBatchResult {
  repeated AuditRecordResult results = 1;     // one per access record and per data change in the request
}

service AuditGrpcService {
  rpc Log(AuditRequest) returns (AuditOperationResult) {}        // unchanged, kept for clients before 10.7
  rpc LogBatch(AuditBatchRequest) returns (AuditBatchResult) {}  // NEW
}
```

## Behavior

| Case | Server | Client |
|---|---|---|
| The whole batch is stored | `STORED` / `DUPLICATE` for every record | Success. |
| The database is unavailable | A gRPC status of `Unavailable`, or every record `RETRYABLE` | Retries the batch with backoff (research R9). |
| Some records are rejected by a processor | Those records are `REJECTED`, the others `STORED` | Counts the rejected records as dropped (`reason=rejected`), with no retry. |
| The server is older than 10.7 | `Unimplemented` | Switches to unary `Log` for the process lifetime and warns once (research R10). |
| The deadline is exceeded | — | Treats the batch as retryable. |

The server writes the batch as the root tenant. Each record keeps its own `TenantId` (research R7).
