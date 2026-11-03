using FluentAssertions;
using Juice.Audit.AspNetCore.Middleware;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Juice.Audit.Tests
{
    public class RequestIsolationTests
    {
        [Fact(DisplayName = "A request should not get the route values of an earlier request")]
        public async Task Request_should_not_get_route_values_of_earlier_request_Async()
        {
            var options = new AuditFilterOptions();
            options.Include("/api/{id}/status");
            // One instance serves every request, as in an app.
            var middleware = new AuditMiddleware(ctx => ctx.Request.Path.Value!.Contains("/2/")
                ? throw new InvalidOperationException("failed")
                : Task.CompletedTask, "test", options);

            var first = await InvokeAsync(middleware, "/api/1/status");
            var failed = await InvokeAsync(middleware, "/api/2/status");

            Data(first).Should().Contain("id", "1");
            Data(failed).Should().NotContainKey("id", "a failed request is not matched, so it has no route values");
        }

        private static Dictionary<string, string> Data(AccessLog accessLog)
            => JsonConvert.DeserializeObject<Dictionary<string, string>>(accessLog.Request!.Data!)!;

        private static async Task<AccessLog> InvokeAsync(AuditMiddleware middleware, string path)
        {
            var auditService = new CapturingAuditService();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuditContextAccessor();
            services.AddSingleton<IAuditService>(auditService);
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Method = "GET";
            context.Request.Path = path;
            try
            {
                await middleware.InvokeAsync(context);
            }
            catch (InvalidOperationException) { }

            return auditService.AccessLog!;
        }

        private class CapturingAuditService : IAuditService
        {
            public AccessLog? AccessLog { get; private set; }

            public Task<IOperationResult> PersistAuditInformationAsync(AccessLog accessLog, DataAudit[] auditEntries, CancellationToken token)
            {
                AccessLog = accessLog;
                return Task.FromResult<IOperationResult>(OperationResult.Success);
            }
        }
    }
}
