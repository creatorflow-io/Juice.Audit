# Juice.Audit

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

A data change with no scope open (an app without these scopes) is skipped. Earlier versions threw there: the change was
lost with a `[DispatchAuditEvents] Error` warning, and the rest of that save's changes were not audited either.

Changes are reported before the transaction commits, so a record of a failed request, command or message can list
changes that were rolled back.

## Time logging

`[TimeLogging]` (optionally with a threshold in ms, and a `ScopeId`) and `AuditFilterOptions.ExecutionTimeThreshold`
ask for a request's timing. The steps measured by the request's `ITimeTracker` (Juice.Measurement) go to:

- **a time store**, when the app registers one (`AddMeasurementEFStores` or the gRPC store): `TimeSummaries` and
  `TimeRecords` under the request's trace id, with the `ScopeId` as the root scope id (the action name without one);
- **the log**, at `Information`, when no store is registered: the tracker's timing table, with the action and trace id.

When an OpenTelemetry tracer records the request, its span also gets `juice.audit.time_logged=true`; find these
requests in the Juice Observability Console with the trace attribute filter `juice.audit.time_logged=true`. In Juice
releases after 10.8 the tracker's scopes are spans of the request as well.
