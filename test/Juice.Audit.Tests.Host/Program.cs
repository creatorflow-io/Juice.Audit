using Juice.Audit;
using Juice.Audit.AspNetCore.Extensions;
using Juice.Audit.AspNetCore.Middleware;
using Juice.Audit.EF;
using Juice.Audit.Tests.Host.Sample;
using Juice.Domain.Events;
using Juice.EF.Extensions;
using Juice.Measurement.Stores.EF;
using Juice.MediatR;

var builder = WebApplication.CreateBuilder(args);


// The audit pipeline in the mode Audit:Mode names (InProcess in appsettings.json; Remote needs Audit:Grpc:Address and a
// broker route for juice.audit). The sample orders' data changes go through their outbox.
builder.Services.AddSampleOrders();
builder.Services.AddMessaging(messaging => messaging
    .AddOutbox()
    .AddIdempotencyInMemory()
    .AddPublishingPolicies(builder.Configuration.GetSection("Messaging:PublishingPolicies"))
    .AddAuditPipeline<SampleOrderContext>(builder.Configuration, options =>
    {
        options.ConfigureOutbox = outbox => outbox.AppName = "audit-test-host";
        //options.ConfigureDb = db => db.DatabaseProvider = "PostgreSQL";
    }));

// Remote mode with a broker (Audit:RabbitMQ:Host): the outbox delivers juice.audit to RabbitMQ, where the collector
// consumes it. The publishing policy routes juice.audit to "rabbitmq" (Messaging:PublishingPolicies).
var rabbit = builder.Configuration.GetSection("Audit:RabbitMQ");
if (!string.IsNullOrEmpty(rabbit["Host"]))
{
    builder.Services.AddMessaging(messaging => messaging
        .AddDelivery(delivery =>
        {
            delivery.AddDeliveryPolicies(_ => { });
            delivery.AddDeliveryProcessor<SampleOrderContext>("rabbitmq");
        }));
    builder.Services.AddEventBus(bus => bus.AddRabbitMQ(r =>
    {
        r.AddConnection("rabbitmq", c =>
        {
            c.Connection = rabbit["Host"];
            c.Port = int.TryParse(rabbit["Port"], out var port) ? port : 5672;
            c.UserName = rabbit["UserName"];
            c.Password = rabbit["Password"];
            c.VirtualHost = rabbit["VirtualHost"];
        });
        r.AddInfrastructureTopology("rabbitmq", t => t.DeclareExchange("juice.audit", "topic"));
        r.AddProducer("rabbitmq", "rabbitmq");
    }));
}

builder.Services.AddExecutionTimeMeasurement();
builder.Services.AddMeasurementEFStores(builder.Configuration, options =>
{
    //options.DatabaseProvider = "SqlServer";
});

builder.Services.AddMediatR(options => { options.RegisterServicesFromAssemblyContaining<Program>(); });

//builder.Services.ConfigureAuditGrpcClient(options =>
//{
//    options.Address = new Uri("https://localhost:7285");
//    options.ChannelOptionsActions.Add(o =>
//    {
//        o.HttpHandler = new SocketsHttpHandler
//        {
//            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
//            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
//            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
//            EnableMultipleHttp2Connections = true
//        };
//    });
//});

builder.Services.AddRazorPages();
builder.Services.AddControllers();

var app = builder.Build();

app.UseStaticFiles();
// The entry point of the sample orders' commands: TransactionBehavior and the outbox need a MessageContext.
app.UseMessageContext("audit-test-host");

var configs = new AuditFilterOptions();
builder.Configuration.Bind("Audit", configs);

app.UseAudit("XUnitTest", options =>
{
    options.ExecutionTimeThreshold = 1000;
    options.Include(string.Empty, "POST", "PUT", "DELETE");
    options.Include("/audit", "GET");
    options.Exclude("/Index");
    options.Include("", new int[] { 403 });
    options.Merge(configs.Filters);
    Console.WriteLine("Filters count: " + options.Filters.Length);
});

app.MapRazorPages();
app.MapDefaultControllerRoute();

app.MapGet("/app", async (ctx) =>
{
    var auditContext = ctx.RequestServices.GetRequiredService<IAuditContextAccessor>().AuditContext;
    await ctx.Response.WriteAsync(auditContext.AccessRecord.Server?.App ?? "");
});

app.MapGet("/request_audit", async (ctx) =>
{
    var mediator = ctx.RequestServices.GetRequiredService<IMediator>();
    await mediator.Publish(new AuditEvent("Inserted")
        .SetAuditRecord(new AuditRecord("test")
        {
            User = "test",
            Database = "test",
            Schema = "test",
            KeyValues = new Dictionary<string, object?>
            {
                { "Id", Guid.NewGuid() }
            },
            CurrentValues = new Dictionary<string, object?>
            {
                { "Name", "test" }
            },
            OriginalValues = new Dictionary<string, object?>
            {
            }
        }));

    await mediator.Publish(new AuditEvent("Inserted")
            .SetAuditRecord(new AuditRecord("test1")
            {
                User = "test",
                Database = "test",
                Schema = "test",
                KeyValues = new Dictionary<string, object?>
                {
                { "Id", Guid.NewGuid() }
                },
                CurrentValues = new Dictionary<string, object?>
                {
                { "Name", "test1" }
                },
                OriginalValues = new Dictionary<string, object?>
                {
                }
            }));
    ctx.Response.StatusCode = 200;
    await ctx.Response.WriteAsync("ok");
});

app.MapGet("/time_exceeded", async (ctx) =>
{
    await Task.Delay(TimeSpan.FromSeconds(3));
    ctx.Response.StatusCode = 200;
    await ctx.Response.WriteAsync("ok");
});

app.MapGet("/request_accesslog", async (ctx) =>
{
    var auditContext = ctx.RequestServices.GetRequiredService<IAuditContextAccessor>().AuditContext;
    auditContext.RequestAccessLog();
    ctx.Response.StatusCode = 200;
    await ctx.Response.WriteAsync("ok");
});


app.MapGet("/err/403", async (ctx) =>
{
    ctx.Response.StatusCode = 403;
    await ctx.Response.WriteAsync("403");
});

app.MapGet("/api/{id}/status", async (int id) =>
{
    return Results.Ok(id);
});
app.MapSampleOrders();

await MigrateAsync(app);

app.Run();

async Task MigrateAsync(WebApplication app)
{
    await app.Services.CreateSampleOrdersAsync();
    using var scope = app.Services.CreateScope();
    // In-process mode only: a remote app carries no audit database.
    var db = scope.ServiceProvider.GetService<AuditDbContext>();
    if (db != null)
    {
        await db.MigrateAsync();
    }
    var db1 = scope.ServiceProvider.GetRequiredService<MeasurementDbContext>();
    await db1.MigrateAsync();
    if (!string.IsNullOrEmpty(app.Configuration["Audit:RabbitMQ:Host"]))
    {
        // The exchange the outbox publishes juice.audit to.
        await app.InitRabbitMQInfrastructureAsync();
    }
}

public partial class Program { }
