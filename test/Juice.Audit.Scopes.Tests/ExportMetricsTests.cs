using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.Collector;
using Juice.Audit.Api.Scopes;
using Juice.Audit.Commands;
using Juice.Audit.Domain;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.Export;
using Juice.MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// Operators can see audit loss and backlog (US3): every lost record is counted with a reason, the losses are
    /// logged as one warning per interval, and the collector counts what it received and stored.
    /// </summary>
    public sealed class ExportMetricsTests
    {
        private const string Records = "juice.audit.export.records";
        private const string Collected = "juice.audit.collector.records";

        private static AccessLog Access() => new("action", "user");

        private static DataAudit Data()
            => new("user", DateTimeOffset.UtcNow, "Modified", null, null, "Orders", "{}", "{}", "trace");

        private static ServiceProvider Build(IAuditExporter exporter, Action<AuditExportOptions> configure,
            LogCapture? logs = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(b =>
            {
                b.SetMinimumLevel(LogLevel.Information);
                if (logs != null)
                {
                    b.AddProvider(logs);
                }
            });
            services.AddMediatR();
            services.AddSingleton(exporter);
            services.AddAuditExport(o =>
            {
                o.MaxBatchSize = 10;
                o.MaxBatchAge = TimeSpan.FromMilliseconds(50);
                o.InitialBackoff = TimeSpan.FromMilliseconds(10);
                o.MaxBackoff = TimeSpan.FromMilliseconds(20);
                o.ShutdownFlushTimeout = TimeSpan.FromMilliseconds(500);
                configure(o);
            });
            services.AddAuditContextAccessor().AddDataEventHandler().AddMediatorAudit();
            return services.BuildServiceProvider();
        }

        private static IHostedService Worker(IServiceProvider provider)
            => provider.GetServices<IHostedService>().OfType<AuditExportWorker>().Single();

        private static async Task PersistAsync(IServiceProvider provider, AccessLog record)
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IAuditService>()
                .PersistAuditInformationAsync(record, Array.Empty<DataAudit>(), default);
        }

        /// <summary>A whole audited unit of work: the runner, the recorder and the queued audit service.</summary>
        private static async Task RunScopeAsync(IServiceProvider provider)
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AuditScopeRunner>().RunAsync(
                new AuditScopeRequest("work", AuditScopeMethods.Request, "work", null),
                () => ValueTask.FromResult(true), _ => AuditScopeOutcome.Ok,
                auditContext => auditContext.RequestAccessLog());
        }

        private static async Task EventuallyAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                watch.ElapsedMilliseconds.Should().BeLessThan(timeoutMs, "the condition should hold by then");
                await Task.Delay(10);
            }
        }

        [Fact(DisplayName = "T-EXPORT-2: drops, exports, retries and failed batches are counted, and the queue size gauge follows the queue")]
        public async Task Export_outcomes_are_counted_Async()
        {
            var exporter = new ScriptedExporter { FailFirst = 1 };
            using var provider = Build(exporter, o => o.QueueCapacity = 3);
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditExportMetrics>().Meter);

            for (var i = 0; i < 5; i++)
            {
                await PersistAsync(provider, Access());
            }
            meter.Sum(Records, ("outcome", "dropped"), ("reason", "queue_full")).Should().Be(2,
                "two records found the queue full");
            meter.Observe("juice.audit.export.queue.size").Should().Be(3);
            meter.Observe("juice.audit.export.queue.capacity").Should().Be(3);

            var worker = Worker(provider);
            await worker.StartAsync(default);
            await EventuallyAsync(() => meter.Sum(Records, ("outcome", "exported")) == 3);
            await worker.StopAsync(default);

            exporter.Calls.Should().Be(2, "the first call failed and the batch was retried once");
            meter.Sum("juice.audit.export.retries").Should().Be(1);
            meter.Sum("juice.audit.export.batches.failed", ("error", nameof(TimeoutException))).Should().Be(1);
            meter.Count("juice.audit.export.duration", ("outcome", "failure")).Should().Be(1);
            meter.Count("juice.audit.export.duration", ("outcome", "success")).Should().Be(1);
            meter.Observe("juice.audit.export.queue.size").Should().Be(0);
        }

        [Fact(DisplayName = "10 000 drops in one interval give one warning with the count and reasons, and no scope warns of its own")]
        public async Task Drops_are_logged_once_per_interval_Async()
        {
            var logs = new LogCapture();
            using var provider = Build(new ScriptedExporter(), o =>
            {
                o.QueueCapacity = 1;
                o.DropWarningInterval = TimeSpan.FromMinutes(1);
            }, logs);
            await PersistAsync(provider, Access()); // fills the queue; no worker runs

            for (var i = 0; i < 10_000; i++)
            {
                await RunScopeAsync(provider);
            }
            logs.Warnings.Should().BeEmpty("the warning waits for the end of its interval");
            provider.GetRequiredService<AuditDropReporter>().Flush(); // the interval ends

            var warning = logs.Warnings.Should().ContainSingle().Subject;
            warning.Category.Should().Be(typeof(AuditDropReporter).FullName);
            warning.Message.Should().StartWith("Dropped 10000 audit records in the last ").And.EndWith("(queue_full: 10000).");
        }

        [Fact(DisplayName = "The drop warning is written when its interval ends, and a new drop opens the next one")]
        public async Task The_warning_follows_the_interval_Async()
        {
            var logs = new LogCapture();
            using var provider = Build(new ScriptedExporter(), o =>
            {
                o.QueueCapacity = 1;
                o.DropWarningInterval = TimeSpan.FromMilliseconds(200);
            }, logs);
            await PersistAsync(provider, Access());

            await PersistAsync(provider, Access());
            await EventuallyAsync(() => logs.Warnings.Count == 1);
            await PersistAsync(provider, Access());
            await PersistAsync(provider, Access());
            await EventuallyAsync(() => logs.Warnings.Count == 2);

            logs.Warnings.Select(w => w.Message).Should().SatisfyRespectively(
                first => first.Should().StartWith("Dropped 1 audit records"),
                second => second.Should().StartWith("Dropped 2 audit records"));
        }

        [Fact(DisplayName = "Shutdown writes the drop warning at once, with each reason")]
        public async Task Shutdown_flushes_the_drop_warning_Async()
        {
            var logs = new LogCapture();
            var exporter = new ScriptedExporter { Hang = true };
            using var provider = Build(exporter, o =>
            {
                o.QueueCapacity = 2;
                o.DropWarningInterval = TimeSpan.FromHours(1);
                o.ShutdownFlushTimeout = TimeSpan.FromMilliseconds(100);
            }, logs);
            for (var i = 0; i < 3; i++)
            {
                await PersistAsync(provider, Access());
            }
            var worker = Worker(provider);
            await worker.StartAsync(default);
            await EventuallyAsync(() => exporter.Calls > 0);

            await worker.StopAsync(default);

            logs.Warnings.Should().ContainSingle().Which.Message.Should()
                .StartWith("Dropped 3 audit records").And.EndWith("(queue_full: 1, shutdown: 2).");
        }

        [Fact(DisplayName = "The collector counts records received, stored and duplicate by kind, with batch sizes and write time")]
        public async Task Collector_metrics_follow_the_batches_Async()
        {
            var services = new ServiceCollection();
            services.AddMediatR();
            services.AddSingleton<IAuditBatchStore, MemoryAuditStore>();
            services.AddAuditCommandHandlers().AddAuditCollectorMetrics();
            using var provider = services.BuildServiceProvider();
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditCollectorMetrics>().Meter);
            var item = new AuditBatchItem(Access(), new[] { Data(), Data() });

            for (var i = 0; i < 2; i++)
            {
                using var scope = provider.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IRequestHandler<SaveAuditBatchCommand, AuditBatchResult>>()
                    .Handle(new SaveAuditBatchCommand(new[] { item }), default);
            }

            meter.Sum(Collected, ("kind", "access"), ("outcome", "received")).Should().Be(2);
            meter.Sum(Collected, ("kind", "access"), ("outcome", "stored")).Should().Be(1);
            meter.Sum(Collected, ("kind", "access"), ("outcome", "duplicate")).Should().Be(1, "the replay");
            meter.Sum(Collected, ("kind", "data"), ("outcome", "received")).Should().Be(4);
            meter.Sum(Collected, ("kind", "data"), ("outcome", "stored")).Should().Be(2);
            meter.Sum(Collected, ("kind", "data"), ("outcome", "duplicate")).Should().Be(2);
            meter.Sum("juice.audit.collector.batch.size", ("kind", "data")).Should().Be(4);
            meter.Count("juice.audit.collector.batch.size", ("kind", "access")).Should().Be(2);
            meter.Count("juice.audit.collector.write.duration", ("kind", "access")).Should().Be(2);
            meter.Count("juice.audit.collector.write.duration", ("kind", "data")).Should().Be(2);
        }

        [Fact(DisplayName = "The collector counts records it couldn't write as failed")]
        public async Task Collector_counts_failures_Async()
        {
            using var metrics = new AuditCollectorMetrics();
            using var meter = new MeterRecorder(metrics.Meter);
            var item = new AuditBatchItem(Access(), new[] { Data() });
            var result = new AuditBatchResult(new[]
            {
                new AuditRecordResult(item.AccessLog!.Id, AuditRecordKind.Access, AuditRecordOutcome.Retryable, "down"),
                new AuditRecordResult(item.DataAudits[0].Id, AuditRecordKind.Data, AuditRecordOutcome.Rejected, "bad")
            });

            metrics.BatchWritten(new[] { item }, result, TimeSpan.FromMilliseconds(5));

            meter.Sum(Collected, ("kind", "access"), ("outcome", "failed")).Should().Be(1);
            meter.Sum(Collected, ("kind", "data"), ("outcome", "rejected")).Should().Be(1);
            await Task.CompletedTask;
        }

        /// <summary>Stores what it is sent, unless told to hang or fail the first calls.</summary>
        private sealed class ScriptedExporter : IAuditExporter
        {
            private int _calls;
            public bool Hang { get; init; }
            public int FailFirst { get; set; }
            public int Calls => _calls;

            public async Task<AuditBatchResult> ExportAsync(IReadOnlyList<AuditExportItem> batch,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                if (Hang)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                if (FailFirst > 0)
                {
                    FailFirst--;
                    throw new TimeoutException("collector unavailable");
                }
                return new AuditBatchResult(batch
                    .Select(i => new AuditRecordResult(i.AccessRecord.Id, AuditRecordKind.Access, AuditRecordOutcome.Stored))
                    .ToList());
            }
        }

        /// <summary>Keeps every warning (or worse) logged, with its category.</summary>
        private sealed class LogCapture : ILoggerProvider
        {
            private readonly List<(string Category, string Message)> _warnings = new();

            public List<(string Category, string Message)> Warnings
            {
                get { lock (_warnings) { return _warnings.ToList(); } }
            }

            public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

            public void Dispose() { }

            private sealed class Logger(string category, LogCapture owner) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                    Func<TState, Exception?, string> formatter)
                {
                    if (logLevel >= LogLevel.Warning)
                    {
                        lock (owner._warnings)
                        {
                            owner._warnings.Add((category, formatter(state, exception)));
                        }
                    }
                }
            }
        }
    }
}
