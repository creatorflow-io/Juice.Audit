using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.EF;
using Juice.EF.Extensions;
using Juice.XUnit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Audit.Tests
{
    /// <summary>
    /// The read API (<c>MapJuiceAudit()</c>) over a real database. Requires <c>ConnectionStrings__SqlServerConnection</c> /
    /// <c>ConnectionStrings__PostgreConnection</c>, like <see cref="MultiTenantDbContextTests"/>.
    /// </summary>
    public class AuditQueryTests
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [IgnoreOnCITheory(DisplayName = "The audit read API lists, pages, filters and groups the records")]
        [InlineData("SqlServer")]
        [InlineData("PostgreSQL")]
        public async Task Read_api_should_list_page_and_filterAsync(string provider)
        {
            await using var app = await StartAsync(provider);
            var client = app.GetTestClient();

            var run = Guid.NewGuid().ToString("N")[..12];
            var user = $"q-{run}";
            var appName = $"app-{run}";
            var trace = Guid.NewGuid().ToString("N");
            var at = DateTimeOffset.UtcNow.AddMinutes(-5);
            var key = Guid.NewGuid().ToString();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
                // Two records at exactly the same time, so paging has to split them; one with a pre-10.4 traceparent id.
                db.AccessLogs.Add(Log(user, appName, at, 200, 15, $"00-{trace}-0123456789abcdef-01"));
                db.AccessLogs.Add(Log(user, appName, at, 500, 2500, Guid.NewGuid().ToString("N")));
                db.AccessLogs.Add(Log(user, appName, at.AddSeconds(-1), 404, 30, Guid.NewGuid().ToString("N")));
                db.AuditEntries.Add(new DataAudit(user, at, "Inserted", "db", "app", $"orders_{run}",
                    JsonSerializer.Serialize(new { Id = key }),
                    JsonSerializer.Serialize(new { OriginalValues = new { }, CurrentValues = new { Name = "a" } }),
                    $"00-{trace}-0123456789abcdef-01"));
                db.AuditEntries.Add(new DataAudit(user, at, "Modified", "db", "app", $"orders_{run}",
                    JsonSerializer.Serialize(new { Id = Guid.NewGuid() }),
                    JsonSerializer.Serialize(new { OriginalValues = new { Name = "a" }, CurrentValues = new { Name = "b" } }),
                    $"00-{trace}-0123456789abcdef-01"));
                await db.SaveChangesAsync();
            }

            var window = $"from={Uri.EscapeDataString(at.AddMinutes(-1).ToString("O"))}&to={Uri.EscapeDataString(at.AddMinutes(1).ToString("O"))}";

            // Paging one row at a time walks all three, newest first, each once.
            var seen = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var page = await GetAsync(client, $"/api/audit/access-logs?{window}&user={user}&limit=1{(cursor == null ? "" : $"&cursor={cursor}")}");
                seen.AddRange(page.GetProperty("items").EnumerateArray());
                cursor = page.GetProperty("nextCursor").GetString();
            } while (cursor != null && seen.Count < 10);
            seen.Select(r => r.GetProperty("id").GetGuid()).Distinct().Should().HaveCount(3);
            seen.Select(r => r.GetProperty("status").GetInt32()).Last().Should().Be(404);
            seen.Should().OnlyContain(r => r.GetProperty("app").GetString() == appName);
            seen.Select(r => r.GetProperty("traceId").GetString()).Should().Contain(trace, "a traceparent is shown as its trace id");

            (await GetAsync(client, $"/api/audit/access-logs?{window}&user={user}&status=5xx"))
                .GetProperty("items").GetArrayLength().Should().Be(1);
            (await GetAsync(client, $"/api/audit/access-logs?{window}&user={user}&status=error"))
                .GetProperty("items").GetArrayLength().Should().Be(2);
            (await GetAsync(client, $"/api/audit/access-logs?{window}&user={user}&minMs=1000"))
                .GetProperty("items").GetArrayLength().Should().Be(1);

            // The keys are a JSON column (jsonb on PostgreSQL): searching inside them.
            var byKey = (await GetAsync(client, $"/api/audit/data-changes?{window}&key={key}")).GetProperty("items");
            byKey.GetArrayLength().Should().Be(1);
            byKey[0].GetProperty("keys").GetProperty("Id").GetString().Should().Be(key);
            (await GetAsync(client, $"/api/audit/data-changes?{window}&table=orders_{run}&action=Modified"))
                .GetProperty("items").GetArrayLength().Should().Be(1);

            var change = await GetAsync(client, $"/api/audit/data-changes/{byKey[0].GetProperty("id").GetGuid()}");
            change.GetProperty("changes").GetProperty("CurrentValues").GetProperty("Name").GetString().Should().Be("a");

            // One trace: the access record stored with a traceparent, and both data changes.
            var traced = await GetAsync(client, $"/api/audit/traces/{trace}");
            traced.GetProperty("accessLogs").GetArrayLength().Should().Be(1);
            traced.GetProperty("accessLogs")[0].GetProperty("dataChanges").GetInt32().Should().Be(2);
            traced.GetProperty("dataChanges").GetArrayLength().Should().Be(2);

            var detail = await GetAsync(client, $"/api/audit/access-logs/{traced.GetProperty("accessLogs")[0].GetProperty("id").GetGuid()}");
            detail.GetProperty("request").GetProperty("path").GetString().Should().Be("/orders");
            detail.GetProperty("metadata").GetProperty("SpanId").GetString().Should().Be("0123456789abcdef");

            var overview = await GetAsync(client, $"/api/audit/overview?{window}");
            overview.GetProperty("apps").EnumerateArray()
                .Should().Contain(a => a.GetProperty("name").GetString() == appName
                    && a.GetProperty("count").GetInt32() == 3 && a.GetProperty("errors").GetInt32() == 2);
            overview.GetProperty("tables").EnumerateArray()
                .Should().Contain(t => t.GetProperty("name").GetString() == $"app.orders_{run}" && t.GetProperty("count").GetInt32() == 2);

            (await client.GetAsync("/api/audit/access-logs?cursor=nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await client.GetAsync($"/api/audit/access-logs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [IgnoreOnCIFact(DisplayName = "The audit read API requires an authenticated caller")]
        public async Task Read_api_should_require_authenticationAsync()
        {
            await using var app = await StartAsync("PostgreSQL", authenticated: false);
            (await app.GetTestClient().GetAsync("/api/audit/overview")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        private static AccessLog Log(string user, string app, DateTimeOffset at, int status, long elapsedMs, string traceId)
        {
            var log = new AccessLog("GET /orders", user) { DateTime = at };
            log.SetRequestInfo(new RequestInfo("GET", "/orders", null, null, null, "https", "10.0.0.1", traceId, "localhost"));
            log.SetServerInfo(new ServerInfo("machine", "os", "1.0", app));
            log.UpdateResponseInfo(r => r.SetResponseInfo(status, "", elapsedMs));
            log.SetMetadata("SpanId", "0123456789abcdef");
            return log;
        }

        private static async Task<JsonElement> GetAsync(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return JsonSerializer.Deserialize<JsonElement>(body, Json);
        }

        private static async Task<WebApplication> StartAsync(string provider, bool authenticated = true)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddAuditDbContext(builder.Configuration, options => options.DatabaseProvider = provider);
            builder.Services.AddAuthentication("test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(new TestAuthHandler.Switch(authenticated));

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapJuiceAudit();

            using (var scope = app.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AuditDbContext>().MigrateAsync();
            }
            await app.StartAsync();
            return app;
        }

        private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public sealed record Switch(bool Authenticated);
            private readonly Switch _switch;

            public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
                UrlEncoder encoder, Switch @switch) : base(options, logger, encoder)
            {
                _switch = @switch;
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
                => Task.FromResult(_switch.Authenticated
                    ? AuthenticateResult.Success(new AuthenticationTicket(
                        new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "tester") }, "test")), "test"))
                    : AuthenticateResult.NoResult());
        }
    }
}
