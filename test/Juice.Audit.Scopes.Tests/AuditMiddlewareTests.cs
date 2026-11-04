using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.AspNetCore.Middleware;
using Juice.MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Juice.Audit.Scopes.Tests.AuditScopeFixture;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// <c>AuditMiddleware</c> after its saving steps moved to <c>AuditRecorder</c>, without the database the
    /// integration tests need; and mediator audit inside a request.
    /// </summary>
    public class AuditMiddlewareTests
    {
        [Fact]
        public async Task Request_that_sends_a_command_is_one_record_Async()
        {
            // AddMediatorAudit() too: the command joins the request's scope.
            using var provider = Build(mediatorAudit: true);
            using var request = new Activity("POST orders").SetIdFormat(ActivityIdFormat.W3C).Start();

            var context = await InvokeAsync(provider, "POST", "/orders/1/pay",
                c => c.RequestServices.GetRequiredService<IMediator>().Send(new ChangeOrder()).AsTask());

            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.AccessLog.Request!.Method.Should().Be("POST");
            saved.AccessLog.Request.Path.Should().Be("/orders/1/pay");
            saved.AccessLog.TraceId.Should().Be(request.TraceId.ToHexString());
            saved.AccessLog.Metadata["SpanId"]!.ToString().Should().Be(request.SpanId.ToHexString());
            saved.AccessLog.Server!.App.Should().Be("orders-api");
            saved.AccessLog.Response!.Status.Should().Be(200);
            ((bool)saved.AccessLog.Metadata["DataAuditRequest"]!).Should().BeTrue();
            saved.DataAudits.Should().ContainSingle().Which.TraceId.Should().Be(request.TraceId.ToHexString());
            context.Response.Headers["X-Trace-Id"].ToString().Should().Be(request.TraceId.ToHexString());
        }

        [Fact]
        public async Task Slow_request_is_time_logged_on_its_span_Async()
        {
            using var provider = Build();
            using var request = new Activity("GET orders").SetIdFormat(ActivityIdFormat.W3C).Start();
            request.ActivityTraceFlags = ActivityTraceFlags.Recorded;

            await InvokeAsync(provider, "GET", "/orders", async _ => await Task.Delay(5),
                o => o.ExecutionTimeThreshold = 1);

            ((bool)provider.Records().Should().ContainSingle().Subject.AccessLog.Metadata["TimeExceeded"]!)
                .Should().BeTrue();
            request.GetTagItem(AuditMiddleware.TimeLoggedTag).Should().Be(true);
        }

        [Fact]
        public async Task Failed_request_is_saved_with_the_error_Async()
        {
            using var provider = Build();

            var invoke = () => InvokeAsync(provider, "POST", "/orders", _ => throw new InvalidOperationException("no stock"));

            await invoke.Should().ThrowAsync<InvalidOperationException>();
            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.AccessLog.Response!.Msg.Should().Be("no stock");
        }

        [Fact]
        public async Task Excluded_request_is_not_saved_Async()
        {
            using var provider = Build();

            await InvokeAsync(provider, "GET", "/health", _ => Task.CompletedTask, o => o.Exclude("/health"));

            provider.Records().Should().BeEmpty();
        }

        private static async Task<HttpContext> InvokeAsync(ServiceProvider provider, string method, string path,
            RequestDelegate next, Action<AuditFilterOptions>? configure = default)
        {
            using var scope = provider.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Method = method;
            context.Request.Path = path;
            var options = new AuditFilterOptions();
            configure?.Invoke(options);
            await new AuditMiddleware(next, "orders-api", options).InvokeAsync(context);
            return context;
        }
    }
}
