using Juice.Audit.AspNetCore.Extensions;
using Juice.EF.Extensions;

// The audit collector (quickstart.md, remote mode): access records over gRPC (LogBatch, and Log for clients before
// 10.7), data changes from the apps' outboxes through RabbitMQ, both processed (header redaction, truncation) and
// written in batches to the audit database it owns.
var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureAuditGrpcHost(builder.Configuration,
    options =>
    {
        options.DatabaseProvider = "PostgreSQL";
    });
builder.Services.AddAuditCollector();

builder.Services.AddMediatR(options =>
{
    options.RegisterServicesFromAssemblyContaining<Program>();
});
builder.Services.AddGrpc(o => o.EnableDetailedErrors = true);

// The data changes, when a broker is configured (Audit:RabbitMQ:Host): the queue the apps route juice.audit to.
var rabbit = builder.Configuration.GetSection("Audit:RabbitMQ");
if (!string.IsNullOrEmpty(rabbit["Host"]))
{
    builder.Services.AddMessaging(m => m.AddIdempotencyInMemory());
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
        // The apps publish juice.audit to the exchange of that name; its queue takes every event sent there.
        var queue = rabbit["Queue"] ?? "juice.audit";
        r.AddInfrastructureTopology("rabbitmq", t => t
            .DeclareExchange("juice.audit", "topic")
            .DeclareQueue(queue)
            .BindQueue(queue, "juice.audit", "#"));
        r.AddConsumer("audit", queue, "rabbitmq", c => c.SubscribeAuditDataChanges());
    }));
}

builder.Services.AddAuthorization();

var app = builder.Build();

// The collector owns the audit database: bring it to the current schema.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<Juice.Audit.EF.AuditDbContext>().MigrateAsync();
}
if (!string.IsNullOrEmpty(rabbit["Host"]))
{
    // The exchange and queue the consumer reads, before it starts.
    await app.InitRabbitMQInfrastructureAsync();
}

app.UseAuthorization();
app.MapAuditGrpcServer();
// A test host has no sign-in: the read API is open here, never in a real collector.
app.MapJuiceAudit("/api/audit").AllowAnonymous();

app.MapGet("/", () => "Hello World!");

app.Run();
