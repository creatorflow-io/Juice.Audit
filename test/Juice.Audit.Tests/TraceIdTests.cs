using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.AspNetCore.Middleware;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Tests
{
    public class TraceIdTests
    {
        [Fact(DisplayName = "Access log should store the W3C trace id and span id of the request")]
        public async Task Access_log_should_store_w3c_trace_idAsync()
        {
            using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

            var (accessLog, header) = await InvokeMiddlewareAsync();

            var traceId = activity.TraceId.ToHexString();
            traceId.Should().MatchRegex("^[0-9a-f]{32}$");
            accessLog!.TraceId.Should().Be(traceId);
            accessLog.Metadata["SpanId"]!.ToString().Should().Be(activity.SpanId.ToHexString());
            header.Should().Be(traceId);
        }

        [Fact(DisplayName = "Access log should fall back to the request id without an activity")]
        public async Task Access_log_should_fall_back_to_request_idAsync()
        {
            Activity.Current = null;

            var (accessLog, header) = await InvokeMiddlewareAsync();

            accessLog!.TraceId.Should().Be("request-1");
            accessLog.Metadata.ContainsKey("SpanId").Should().BeFalse();
            header.Should().Be("request-1");
        }

        private static async Task<(AccessLog? AccessLog, string? Header)> InvokeMiddlewareAsync()
        {
            var auditService = new CapturingAuditService();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuditContextAccessor();
            services.AddSingleton<IAuditService>(auditService);
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, TraceIdentifier = "request-1" };
            context.Request.Method = "GET";
            context.Request.Path = "/api/test";

            var middleware = new AuditMiddleware(_ => Task.CompletedTask, "test", new AuditFilterOptions());
            await middleware.InvokeAsync(context);

            return (auditService.AccessLog, context.Response.Headers["X-Trace-Id"].FirstOrDefault());
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
