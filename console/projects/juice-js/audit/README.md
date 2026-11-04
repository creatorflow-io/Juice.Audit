# @juice-js/audit

The Juice Audit pages for an Angular app built on `@juice-js/layout`:
- **Overview:** totals, failures, users, apps and changed tables over a time window.
- **Access records:** the audited requests, messages and jobs, with each one's request, response, server and metadata.
- **Data changes:** each changed row's values before and after, and its history.
- **Trace:** everything audited under one trace id.

The pages read the audit API that `Juice.Audit.Api` maps with `MapJuiceAudit()`.

## Host (ASP.NET Core)

```csharp
builder.Services.ConfigureAuditDefault(builder.Configuration);   // or ConfigureAuditGrpcHost() on the audit server
// …
app.UseAuthentication();
app.UseAuthorization();
app.MapJuiceAudit("/api/audit");                 // any authenticated user (the default policy)
app.MapJuiceAudit("/api/audit", "AuditReaders"); // or a policy of yours
```

The records are read through `AuditDbContext`. A tenant sees only its own records, and the root tenant sees all of them. Map the API where the audit database is: in the app itself with `ConfigureAuditDefault()`, or on the gRPC audit server that its apps send to.

## App

```ts
// app module or app config
providers: [
  provideHttpClient(withInterceptorsFromDi()),
  provideJuiceAudit({ apiBase: '', basePath: '/audit' }),
],

// routes: mount the pages under basePath, behind your own sign-in guard
{ path: 'audit', children: AUDIT_ROUTES.map(r => ({ ...r, canActivate: [AuthGuard] })) },

// menu (juice-layout)
MenuModule.forRoot([...yourMenus, ...auditMenus('/audit', 200)]),
```

```scss
// styles.scss (the app's global styles, once): the pages' layout, in the Juice Console's teal theme, light and dark (.dark).
@use '@juice-js/audit';

// Optional: colours from your own Angular Material (M2) themes instead.
:root { @include audit.colors($light-theme); }
.dark { @include audit.colors($dark-theme); }       // under your dark-mode selector
```

`colors($theme, $tones: ())` sets the `--ja-*` custom properties the pages use:
- **Text and surfaces:** the theme's background and foreground colours.
- **Links and selection:** the theme's primary palette.
- **Failures:** the theme's warn palette.
- **Tones:** succeeded, warning, modified and neutral default to Material's green, deep orange, blue and greys. Pass `$tones: (ok: …, warn: …, bad: …, info: …, none: …)` to change them.

The default colours use zero-specificity `:where()` selectors, so your `colors()` replaces them wherever it sits in the file. The pages don't style the page itself, so give `body` your own colours.

- `apiBase`: the API origin; empty when the API serves the app.
- `apiPath`: where `MapJuiceAudit()` mapped the API; `/api/audit` by default.
- `basePath`: where the routes are mounted; every link between the pages goes under it.
- `traceLink`: optional. Turns each trace id into a link to your tracing UI, as a route (`id => ['/observability/traces', id]`) or a URL string.
- The router needs `bindToComponentInputs: true`, because the pages take their route and query parameters as inputs.
- The host's bearer-token interceptor (`@juice-js/auth`) must cover `apiBase`.

## With @juice-js/observability

The two packages can share one app.
- **No collisions:** classes start with `ja-` here and `jc-` there, and menu ids start with `audit-`.
- **Mounting:** mount each package's routes at its own path.
- **Theme:** pass your theme to both `colors()` mixins.
- **Traces:** set `traceLink` to the observability trace page, so an audit record opens its spans and logs:

```ts
provideJuiceObservability({ basePath: '/observability' }),
provideJuiceAudit({ basePath: '/audit', traceLink: id => ['/observability/traces', id] }),
```
