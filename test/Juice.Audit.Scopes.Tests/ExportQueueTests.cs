using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.Api.Scopes;
using Juice.Audit.Commands;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.Export;
using Juice.MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// Access records leave the app in the background (US2): a scope only queues its record; the worker batches by
    /// size and age, retries with backoff, flushes on shutdown, and counts every record it loses.
    /// </summary>
    public sealed class ExportQueueTests
    {
        private const string Records = "juice.audit.export.records";

        private static AccessLog Access() => new("action", "user");

        private static ServiceProvider Build(ScriptedExporter exporter, Action<AuditExportOptions>? configure = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediatR();
            services.AddSingleton<IAuditExporter>(exporter);
            services.AddAuditExport(o =>
            {
                o.MaxBatchSize = 3;
                o.MaxBatchAge = TimeSpan.FromMilliseconds(100);
                o.InitialBackoff = TimeSpan.FromMilliseconds(10);
                o.MaxBackoff = TimeSpan.FromMilliseconds(50);
                o.MaxRetryAttempts = 3;
                o.ShutdownFlushTimeout = TimeSpan.FromMilliseconds(500);
                configure?.Invoke(o);
            });
            services.AddAuditContextAccessor().AddDataEventHandler().AddMediatorAudit();
            return services.BuildServiceProvider();
        }

        private static IHostedService Worker(IServiceProvider provider)
            => provider.GetServices<IHostedService>().OfType<AuditExportWorker>().Single();

        private static async Task<IOperationResult> PersistAsync(IServiceProvider provider, AccessLog record)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAuditService>()
                .PersistAuditInformationAsync(record, Array.Empty<DataAudit>(), default);
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

        [Fact(DisplayName = "T-EXPORT-1: with an exporter that never answers, handing off a record doesn't wait, and the scope completes")]
        public async Task Handing_off_never_waits_Async()
        {
            var exporter = new ScriptedExporter { Hang = true };
            using var provider = Build(exporter);
            var worker = Worker(provider);
            await worker.StartAsync(default);

            var handoff = PersistAsync(provider, Access());
            handoff.IsCompleted.Should().BeTrue("queuing is an in-memory write");
            (await handoff).Succeeded.Should().BeTrue();

            // A whole audited unit of work, while the exporter hangs on the first record.
            await EventuallyAsync(() => exporter.Calls > 0);
            var watch = Stopwatch.StartNew();
            using (var scope = provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AuditScopeRunner>().RunAsync(
                    new AuditScopeRequest("work", AuditScopeMethods.Request, "work", null),
                    () => ValueTask.FromResult(true), _ => AuditScopeOutcome.Ok,
                    auditContext => auditContext.RequestAccessLog());
            }
            watch.ElapsedMilliseconds.Should().BeLessThan(200);
            provider.GetRequiredService<AuditExportQueue>().Count.Should().Be(1, "the scope's record is queued");
        }

        [Fact(DisplayName = "A batch goes when it is full, and a partial one when its first record is old enough")]
        public async Task Batches_go_by_size_or_age_Async()
        {
            var exporter = new ScriptedExporter();
            using var provider = Build(exporter);
            var worker = Worker(provider);
            for (var i = 0; i < 7; i++)
            {
                await PersistAsync(provider, Access());
            }

            await worker.StartAsync(default);
            await EventuallyAsync(() => exporter.Batches.Sum(b => b.Count) == 7);
            await worker.StopAsync(default);

            exporter.Batches.Select(b => b.Count).Should().Equal(3, 3, 1);
        }

        [Fact(DisplayName = "A record waits at most MaxBatchAge for others to join it")]
        public async Task A_lone_record_waits_for_the_batch_age_Async()
        {
            var exporter = new ScriptedExporter();
            using var provider = Build(exporter, o => o.MaxBatchAge = TimeSpan.FromMilliseconds(300));
            var worker = Worker(provider);
            await worker.StartAsync(default);

            var watch = Stopwatch.StartNew();
            await PersistAsync(provider, Access());
            await EventuallyAsync(() => exporter.Calls == 1);

            watch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(250);
            await worker.StopAsync(default);
        }

        [Fact(DisplayName = "A failed batch is retried with backoff, and the retries are counted")]
        public async Task A_failed_batch_is_retried_Async()
        {
            var exporter = new ScriptedExporter { FailFirst = 2 };
            using var provider = Build(exporter);
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditExportMetrics>().Meter);
            var worker = Worker(provider);
            await PersistAsync(provider, Access());

            await worker.StartAsync(default);
            await EventuallyAsync(() => exporter.Stored.Count == 1);
            await worker.StopAsync(default);

            exporter.Calls.Should().Be(3);
            meter.Sum("juice.audit.export.retries").Should().Be(2);
            meter.Sum("juice.audit.export.batches.failed").Should().Be(2);
            meter.Sum(Records, ("outcome", "exported")).Should().Be(1);
        }

        [Fact(DisplayName = "Only retryable records are retried; refused ones are dropped and counted")]
        public async Task Only_retryable_records_are_retried_Async()
        {
            var refused = Access();
            var transient = Access();
            var exporter = new ScriptedExporter();
            exporter.Answers[refused.Id] = new Queue<AuditRecordOutcome>(new[] { AuditRecordOutcome.Rejected });
            exporter.Answers[transient.Id] = new Queue<AuditRecordOutcome>(new[] { AuditRecordOutcome.Retryable });
            using var provider = Build(exporter);
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditExportMetrics>().Meter);
            var worker = Worker(provider);
            await PersistAsync(provider, refused);
            await PersistAsync(provider, transient);

            await worker.StartAsync(default);
            await EventuallyAsync(() => exporter.Stored.Contains(transient.Id));
            await worker.StopAsync(default);

            exporter.Batches[1].Select(i => i.AccessRecord.Id).Should().Equal(transient.Id);
            meter.Sum(Records, ("outcome", "dropped"), ("reason", "rejected")).Should().Be(1);
            meter.Sum(Records, ("outcome", "exported")).Should().Be(1);
        }

        [Fact(DisplayName = "A batch that keeps failing is dropped and counted, and the worker goes on")]
        public async Task Retries_exhausted_are_counted_and_the_worker_goes_on_Async()
        {
            var exporter = new ScriptedExporter { FailFirst = 4 };
            using var provider = Build(exporter);
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditExportMetrics>().Meter);
            var worker = Worker(provider);
            await worker.StartAsync(default);

            await PersistAsync(provider, Access());
            await EventuallyAsync(() => meter.Sum(Records, ("outcome", "dropped")) == 1);
            await PersistAsync(provider, Access());
            await EventuallyAsync(() => exporter.Stored.Count == 1);
            await worker.StopAsync(default);

            meter.Sum(Records, ("outcome", "dropped"), ("reason", "retries_exhausted")).Should().Be(1);
            exporter.Calls.Should().Be(5, "four failed attempts (one and three retries), then the next batch");
        }

        [Fact(DisplayName = "On shutdown the queue is flushed")]
        public async Task The_queue_is_flushed_on_shutdown_Async()
        {
            var exporter = new ScriptedExporter();
            using var provider = Build(exporter, o => o.MaxBatchAge = TimeSpan.FromMinutes(1));
            var worker = Worker(provider);
            await worker.StartAsync(default);
            await PersistAsync(provider, Access());
            await PersistAsync(provider, Access());

            await worker.StopAsync(default);

            exporter.Stored.Should().HaveCount(2);
        }

        [Fact(DisplayName = "What can't be sent within the flush timeout is dropped and counted")]
        public async Task What_isnt_flushed_in_time_is_counted_Async()
        {
            var exporter = new ScriptedExporter { Hang = true };
            using var provider = Build(exporter, o => o.ShutdownFlushTimeout = TimeSpan.FromMilliseconds(200));
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditExportMetrics>().Meter);
            var worker = Worker(provider);
            await worker.StartAsync(default);
            for (var i = 0; i < 5; i++)
            {
                await PersistAsync(provider, Access());
            }
            await EventuallyAsync(() => exporter.Calls > 0);

            var watch = Stopwatch.StartNew();
            await worker.StopAsync(default);

            watch.ElapsedMilliseconds.Should().BeLessThan(2000);
            meter.Sum(Records, ("outcome", "dropped"), ("reason", "shutdown")).Should().Be(5);
        }

        [Fact(DisplayName = "A full queue drops the new record, counts it, and keeps the queued ones")]
        public async Task A_full_queue_drops_new_records_Async()
        {
            var exporter = new ScriptedExporter();
            using var provider = Build(exporter, o => o.QueueCapacity = 2);
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditExportMetrics>().Meter);
            var first = Access();

            await PersistAsync(provider, first);
            await PersistAsync(provider, Access());
            for (var i = 0; i < 3; i++)
            {
                (await PersistAsync(provider, Access())).Succeeded.Should().BeTrue(
                    "a drop is counted, not reported to the scope, which would log once per record");
            }

            meter.Sum(Records, ("outcome", "dropped"), ("reason", "queue_full")).Should().Be(3);
            var worker = Worker(provider);
            await worker.StartAsync(default);
            await worker.StopAsync(default);
            exporter.Stored.Should().HaveCount(2).And.Contain(first.Id);
        }

        [Fact(DisplayName = "Synchronous mode exports inside the scope and returns the collector's answer")]
        public async Task Synchronous_mode_exports_inline_Async()
        {
            var exporter = new ScriptedExporter();
            using var provider = Build(exporter, o => o.Synchronous = true);
            var record = Access();

            var result = await PersistAsync(provider, record);

            result.Succeeded.Should().BeTrue();
            exporter.Stored.Should().ContainSingle().Which.Should().Be(record.Id);
            provider.GetRequiredService<AuditExportQueue>().Count.Should().Be(0);
        }

        [Fact(DisplayName = "Synchronous mode reports a failed export to the scope")]
        public async Task Synchronous_mode_reports_failures_Async()
        {
            var exporter = new ScriptedExporter { FailFirst = 1 };
            using var provider = Build(exporter, o => o.Synchronous = true);

            var result = await PersistAsync(provider, Access());

            result.Succeeded.Should().BeFalse();
        }

        /// <summary>
        /// Stores what it is sent, unless told to hang, fail the first calls, or answer given outcomes for given ids
        /// (once each, then stored).
        /// </summary>
        private sealed class ScriptedExporter : IAuditExporter
        {
            private int _calls;
            private readonly List<List<AuditExportItem>> _batches = new();
            private readonly List<Guid> _stored = new();
            public bool Hang { get; init; }
            public int FailFirst { get; set; }
            public int Calls => _calls;

            /// <summary>Snapshots: the worker adds from its own thread while the test polls.</summary>
            public List<List<AuditExportItem>> Batches
            {
                get { lock (_batches) { return _batches.ToList(); } }
            }

            public List<Guid> Stored
            {
                get { lock (_stored) { return _stored.ToList(); } }
            }
            public Dictionary<Guid, Queue<AuditRecordOutcome>> Answers { get; } = new();

            public async Task<AuditBatchResult> ExportAsync(IReadOnlyList<AuditExportItem> batch,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                lock (_batches)
                {
                    _batches.Add(batch.ToList());
                }
                if (Hang)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                if (FailFirst > 0)
                {
                    FailFirst--;
                    throw new TimeoutException("collector unavailable");
                }
                var results = new List<AuditRecordResult>();
                foreach (var item in batch)
                {
                    var outcome = Answers.TryGetValue(item.AccessRecord.Id, out var answers) && answers.Count > 0
                        ? answers.Dequeue()
                        : AuditRecordOutcome.Stored;
                    if (outcome == AuditRecordOutcome.Stored)
                    {
                        lock (_stored)
                        {
                            _stored.Add(item.AccessRecord.Id);
                        }
                    }
                    results.Add(new AuditRecordResult(item.AccessRecord.Id, AuditRecordKind.Access, outcome));
                }
                return new AuditBatchResult(results);
            }
        }
    }
}
