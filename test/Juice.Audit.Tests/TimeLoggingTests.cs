using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.AspNetCore.Middleware;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Measurement.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Audit.Tests
{
    public class TimeLoggingTests
    {
        [Fact(DisplayName = "A registered time store should get the records, and a traced request's span the tag")]
        public async Task Traced_request_should_save_and_tag_Async()
        {
            using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
            activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;
            var timeRepository = new CapturingTimeRepository();

            await InvokeMiddlewareAsync(timeRepository);

            activity.GetTagItem(AuditMiddleware.TimeLoggedTag).Should().Be(true);
            timeRepository.Saved.Should().ContainSingle().Which.TraceId.Should().Be(activity.TraceId.ToHexString());
        }

        [Fact(DisplayName = "An untraced request should save to a registered time store")]
        public async Task Untraced_request_should_save_Async()
        {
            Activity.Current = null;
            var timeRepository = new CapturingTimeRepository();

            await InvokeMiddlewareAsync(timeRepository);

            timeRepository.Saved.Should().ContainSingle().Which.TraceId.Should().Be("request-1");
        }

        [Fact(DisplayName = "The time store should get the ScopeId of [TimeLogging]")]
        public async Task Time_store_should_get_the_scope_id_Async()
        {
            Activity.Current = null;
            var timeRepository = new CapturingTimeRepository();

            await InvokeMiddlewareAsync(timeRepository, scopeId: "orders.checkout");

            var summary = timeRepository.Saved.Should().ContainSingle().Subject;
            summary.RootScopeId.Should().Be("orders.checkout");
            summary.Name.Should().Be("api_test");
        }

        [Fact(DisplayName = "Without a time store the timing should be logged")]
        public async Task Without_a_store_the_timing_should_be_logged_Async()
        {
            Activity.Current = null;

            var logs = await InvokeMiddlewareAsync(timeRepository: null);

            logs.Should().Contain(l => l.Level == LogLevel.Information
                && l.Message.StartsWith("Execution time of api_test, trace request-1") && l.Message.Contains("Invoke"));
        }

        /// <summary>A request whose handler asks for time logging, as <c>[TimeLogging]</c> does.</summary>
        private static async Task<IReadOnlyCollection<(LogLevel Level, string Message)>> InvokeMiddlewareAsync(
            ITimeRepository? timeRepository, string? scopeId = null)
        {
            var logs = new CapturingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Information));
            services.AddAuditContextAccessor();
            services.AddSingleton<IAuditService>(new NoAuditService());
            if (timeRepository != null)
            {
                services.AddSingleton(timeRepository);
            }
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, TraceIdentifier = "request-1" };
            context.Request.Method = "GET";
            context.Request.Path = "/api/test";

            var middleware = new AuditMiddleware(ctx =>
            {
                var auditContext = ctx.RequestServices.GetRequiredService<IAuditContextAccessor>().AuditContext;
                auditContext.RequestMeasureLog(null);
                if (scopeId != null)
                {
                    auditContext.Items["ScopeId"] = scopeId;
                }
                return Task.CompletedTask;
            }, "test", new AuditFilterOptions());
            await middleware.InvokeAsync(context);

            return logs.Messages.ToArray();
        }

        private class NoAuditService : IAuditService
        {
            public Task<IOperationResult> PersistAuditInformationAsync(AccessLog accessLog, DataAudit[] auditEntries, CancellationToken token)
                => Task.FromResult<IOperationResult>(OperationResult.Success);
        }

        private class CapturingTimeRepository : ITimeRepository
        {
            public List<TimeSummary> Saved { get; } = new();

            public Task<IEnumerable<TimeRecord>> GetTimeRecordsAsync(string traceId, CancellationToken token)
                => Task.FromResult(Enumerable.Empty<TimeRecord>());
            public Task<TimeSummary?> GetTimeSummaryAsync(string traceId) => Task.FromResult<TimeSummary?>(null);
            public Task SaveTrackDataAsync(TimeSummary summary, IEnumerable<TimeRecord> records)
            {
                Saved.Add(summary);
                return Task.CompletedTask;
            }
        }

        private class CapturingLoggerProvider : ILoggerProvider, ILogger
        {
            public ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();

            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Enqueue((logLevel, formatter(state, exception)));
            public void Dispose() { }
        }
    }
}
