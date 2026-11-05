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

A data change with no scope open (an app without these scopes) is skipped. Earlier versions threw there: the change was
lost with a `[DispatchAuditEvents] Error` warning, and the rest of that save's changes were not audited either.

A change made inside a transaction managed by Juice's `TransactionBehavior` is saved only if the transaction commits.
Otherwise it is dropped: the record of the attempt is still saved, with the number of dropped changes in its metadata
(`UncommittedChanges`). A transaction the app opens itself is not tracked: its changes are saved even if it rolls back.

A data change with no user of its own (a DbContext knows one only through `IHttpContextAccessor`) takes the user of its
scope.

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
