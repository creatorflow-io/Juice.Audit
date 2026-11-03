# Juice.Audit

## Trace id

Every access log (`Req_TraceId`) and data change (`TraceId`) records the W3C trace id of the request that wrote it:
32 hexadecimal characters, the id OpenTelemetry back ends such as Juice Observability show. The response carries the
same id in the `X-Trace-Id` header, and the access log's metadata holds the request span's id under `SpanId`. Open
`{console}/traces/{traceId}` in the Juice Observability Console to see the request's spans and logs.

Without an active W3C activity, the id falls back to ASP.NET's `HttpContext.TraceIdentifier`. Before 10.4 the id was
the whole `traceparent` (`00-<trace id>-<span id>-01`); the Juice Observability Console accepts that form as well.

## Time logging

`[TimeLogging]` (optionally with a threshold in ms, and a `ScopeId`) and `AuditFilterOptions.ExecutionTimeThreshold`
ask for a request's timing. The steps measured by the request's `ITimeTracker` (Juice.Measurement) go to:

- **a time store**, when the app registers one (`AddMeasurementEFStores` or the gRPC store): `TimeSummaries` and
  `TimeRecords` under the request's trace id, with the `ScopeId` as the root scope id (the action name without one);
- **the log**, at `Information`, when no store is registered: the tracker's timing table, with the action and trace id.

When an OpenTelemetry tracer records the request, its span also gets `juice.audit.time_logged=true`; find these
requests in the Juice Observability Console with the trace attribute filter `juice.audit.time_logged=true`. In Juice
releases after 10.8 the tracker's scopes are spans of the request as well.
