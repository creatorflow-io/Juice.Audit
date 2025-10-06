using Juice.Audit;
using Juice.Audit.AspNetCore.Extensions;
using Juice.Audit.AspNetCore.Middleware;
using Juice.Audit.EF;
using Juice.Domain.Events;
using Juice.EF.Extensions;
using Juice.Measurement.Stores.EF;
using Juice.MediatR;

var builder = WebApplication.CreateBuilder(args);


builder.Services.ConfigureAuditDefault(builder.Configuration, options =>
{
    //options.DatabaseProvider = "PostgreSQL";
});

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
// Use with ConfigureAuditDefault together
await MigrateAsync(app);

app.Run();

async Task MigrateAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
    await db.MigrateAsync();
    var db1 = scope.ServiceProvider.GetRequiredService<MeasurementDbContext>();
    await db1.MigrateAsync();
}

public partial class Program { }
