// The audit collector: access records over gRPC (LogBatch, and Log for apps before 10.7), data changes from the apps'
// outboxes through RabbitMQ, both processed (header redaction, truncation) and written in batches to the audit
// database it owns. Configuration: README.MD, "Running the collector".
//
//   dotnet Juice.Audit.Collector.dll             run
//   dotnet Juice.Audit.Collector.dll --migrate   bring the audit database to the current schema, then exit
using Juice.Audit.AspNetCore.Extensions;
using Juice.Audit.Collector.Host;
using Juice.Audit.EF;
using Juice.EF.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var migrateOnly = args.Contains("--migrate");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--migrate").ToArray());
var settings = CollectorSettings.Read(builder.Configuration);
builder.Services.AddSingleton(settings);

// The audit database.
builder.Services.ConfigureAuditGrpcHost(builder.Configuration, db =>
{
    db.DatabaseProvider = settings.Database.NormalizedProvider!;
    db.Schema = settings.Database.Schema;
    db.ConnectionName = settings.Database.ConnectionName;
});
builder.Services.AddAuditCollector();
builder.Services.AddMediatR(options => options.RegisterServicesFromAssemblyContaining<CollectorSettings>());

if (migrateOnly)
{
    await using var migrator = builder.Build();
    await MigrateAsync(migrator.Services);
    return;
}

// Access records: gRPC, behind the API keys when there are any (the only place that decides it).
var requireKey = settings.Grpc.ApiKeys.Length > 0;
if (requireKey)
{
    builder.Services.AddSingleton<ApiKeyInterceptor>();
}
builder.Services.AddGrpc(o =>
{
    if (requireKey)
    {
        o.Interceptors.Add<ApiKeyInterceptor>();
    }
});

// Data changes: the queue the apps route juice.audit to, bound to its exchange. The inbox drops a message delivered
// twice; the batch writer also skips the ids it already has, so a restart that forgets the inbox stores nothing twice.
if (settings.HasBroker)
{
    var rabbit = settings.RabbitMQ;
    builder.Services.AddMessaging(m => m.AddIdempotencyInMemory());
    builder.Services.AddEventBus(bus => bus.AddRabbitMQ(r =>
    {
        r.AddConnection("rabbitmq", c =>
        {
            c.Connection = rabbit.Host;
            c.Port = rabbit.Port;
            c.UserName = rabbit.UserName;
            c.Password = rabbit.Password;
            c.VirtualHost = rabbit.VirtualHost;
        });
        r.AddInfrastructureTopology("rabbitmq", t => t
            .DeclareExchange(rabbit.Exchange, "topic")
            .DeclareQueue(rabbit.Queue)
            .BindQueue(rabbit.Queue, rabbit.Exchange, "#"));
        r.AddConsumer("audit", rabbit.Queue, "rabbitmq", c => c.SubscribeAuditDataChanges());
    }));
}

// The read API, signed in with bearer tokens unless it's explicitly open. Authentication is always registered, as
// UseAuthentication() needs it, with no scheme when nothing signs in.
if (settings.ReadApi.Enabled && !settings.ReadApi.AllowAnonymous)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        var jwt = settings.ReadApi.Jwt;
        o.Authority = jwt.Authority;
        o.Audience = jwt.Audience;
        o.RequireHttpsMetadata = jwt.RequireHttpsMetadata;
        if (!string.IsNullOrWhiteSpace(jwt.MetadataAddress))
        {
            o.MetadataAddress = jwt.MetadataAddress;
        }
    });
}
else
{
    builder.Services.AddAuthentication();
}
builder.Services.AddAuthorization(o =>
{
    // A token that grants one of Collector:ReadApi:RequiredScopes, as "scope" (space separated) or "scp" claims. The
    // bearer handler maps inbound "scp" (Entra ID's) to the long scope claim type, so that one counts too.
    var required = settings.ReadApi.RequiredScopes;
    if (required.Length > 0)
    {
        o.AddPolicy(ReadApiSettings.PolicyName, p => p.RequireAuthenticatedUser().RequireAssertion(ctx => ctx.User.Claims
            .Where(c => c.Type is "scope" or "scp" or "http://schemas.microsoft.com/identity/claims/scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(s => required.Contains(s, StringComparer.Ordinal))));
    }
});

builder.Services.AddHealthChecks()
    .AddCheck<AuditDatabaseHealthCheck>("audit-database", tags: new[] { "ready" });

// Its own telemetry, when an OTLP endpoint is set (OTEL_EXPORTER_OTLP_ENDPOINT): the Juice.Audit meter
// (juice.audit.collector.*) and the gRPC calls' spans.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "juice-audit-collector"))
        .WithMetrics(m => m
            .AddMeter("Juice.Audit")
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter())
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
            .AddOtlpExporter());
}

var app = builder.Build();

if (settings.Database.Migrate)
{
    await MigrateAsync(app.Services);
}
if (settings.HasBroker)
{
    // The exchange and queue the consumer reads, before it starts.
    await app.InitRabbitMQInfrastructureAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAuditGrpcServer();

// Live: the process answers. Ready: the audit database answers too.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

if (settings.ReadApi.Enabled)
{
    var readApi = app.MapJuiceAudit(settings.ReadApi.Path,
        settings.ReadApi.RequiredScopes.Length > 0 ? ReadApiSettings.PolicyName : null);
    if (settings.ReadApi.AllowAnonymous)
    {
        readApi.AllowAnonymous();
    }
    // Only on the HTTP endpoint (Kestrel:Endpoints:Http, 8081 in the image): the gRPC port is opened to the apps and
    // would otherwise serve the read API too, over HTTP/2.
    if (PortOf(builder.Configuration["Kestrel:Endpoints:Http:Url"]) is int httpPort)
    {
        readApi.AddEndpointFilter((context, next) => context.HttpContext.Connection.LocalPort == httpPort
            ? next(context)
            : ValueTask.FromResult<object?>(Results.NotFound()));
    }
}

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Juice.Audit.Collector");
logger.LogInformation("Audit collector: database {Provider} (schema {Schema}); data changes {Broker}; gRPC {GrpcAuth}; read API {ReadApi}.",
    settings.Database.NormalizedProvider, settings.Database.Schema,
    settings.HasBroker ? $"from RabbitMQ queue {settings.RabbitMQ.Queue}" : "not consumed (no broker)",
    settings.Grpc.ApiKeys.Length > 0 ? $"{settings.Grpc.ApiKeys.Length} API key(s)" : "open",
    !settings.ReadApi.Enabled ? "off" : settings.ReadApi.AllowAnonymous ? $"{settings.ReadApi.Path}, open" : settings.ReadApi.Path);
if (settings.ReadApi.Enabled && PortOf(builder.Configuration["Kestrel:Endpoints:Http:Url"]) == null)
{
    // Whatever opened the ports (other endpoint names, ASPNETCORE_URLS, a dynamic port): the read API can't be kept
    // to one of them.
    logger.LogWarning("The read API answers on every port the collector listens on, the gRPC port too: name its port " +
        "in Kestrel:Endpoints:Http:Url (8081 in the image), and gRPC's in Kestrel:Endpoints:Grpc:Url.");
}
if (settings.Grpc.ApiKeys.Length == 0)
{
    logger.LogWarning("The gRPC service takes records from anyone who can reach it: set Collector:Grpc:ApiKeys to require a key.");
}

app.Run();

static async Task MigrateAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AuditDbContext>().MigrateAsync();
}

// The port of a Kestrel endpoint URL such as http://+:8081; null when it names none, or 0 (a port chosen at bind
// time, which a request's local port would never match).
static int? PortOf(string? url)
    => url != null && int.TryParse(url[(url.LastIndexOf(':') + 1)..].TrimEnd('/'), out var port) && port > 0
        ? port : null;
