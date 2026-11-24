using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.Commands;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.Export;
using Juice.Audit.Services;
using Juice.MediatR;
using Juice.MultiTenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// What a queued, batched export must keep from the request that made the record: its tenant and its time, and
    /// an account of every record lost, data changes included.
    /// </summary>
    public sealed class ExportCorrectnessTests
    {
        private static DataAudit Data(string? tenantId = null)
            => new("user", DateTimeOffset.UtcNow, "Modified", null, null, "Orders", "{}", "{}", "trace", tenantId);

        private static ServiceProvider Build(IAuditExporter exporter, Action<AuditExportOptions>? configure = null,
            string? tenant = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediatR();
            services.AddSingleton(exporter);
            services.AddAuditExport(o =>
            {
                o.MaxBatchAge = TimeSpan.FromMilliseconds(20);
                configure?.Invoke(o);
            });
            if (tenant != null)
            {
                services.AddScoped<ITenantAccessor>(_ => new FixedTenant(tenant));
            }
            return services.BuildServiceProvider();
        }

        private static async Task PersistAsync(IServiceProvider provider, AccessLog record, params DataAudit[] entries)
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IAuditService>()
                .PersistAuditInformationAsync(record, entries, default);
        }

        [Fact(DisplayName = "A queued record keeps the tenant of the request that made it, not the writer's (none)")]
        public async Task Queued_records_keep_the_request_tenant_Async()
        {
            var exporter = new CapturingExporter();
            using var provider = Build(exporter, o => o.Synchronous = true, tenant: "acme");
            var untenanted = new AccessLog("action", "user");
            var own = Data("other");

            await PersistAsync(provider, untenanted, Data(), own);

            var item = exporter.Items.Should().ContainSingle().Subject;
            item.AccessRecord.TenantId.Should().Be("acme");
            item.BestEffortEntries.Select(e => e.TenantId).Should().Equal("acme", "other");
            own.TenantId.Should().Be("other", "a tenant already set is never overwritten");
        }

        [Fact(DisplayName = "A data change the collector refuses is counted as lost, though its access record is stored")]
        public async Task Refused_data_changes_are_counted_Async()
        {
            var exporter = new CapturingExporter { RejectData = true };
            using var provider = Build(exporter);
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditExportMetrics>().Meter);
            var worker = provider.GetServices<IHostedService>().OfType<AuditExportWorker>().Single();
            await worker.StartAsync(default);

            await PersistAsync(provider, new AccessLog("action", "user"), Data(), Data());

            var watch = Stopwatch.StartNew();
            while (meter.Sum("juice.audit.export.records", ("outcome", "exported")) < 1)
            {
                watch.ElapsedMilliseconds.Should().BeLessThan(5000);
                await Task.Delay(10);
            }
            await worker.StopAsync(default);
            meter.Sum("juice.audit.export.records", ("outcome", "dropped"), ("reason", "rejected")).Should().Be(2);
            exporter.Calls.Should().Be(1, "a refusal is not retried");
        }

        [Fact(DisplayName = "A record an old collector may have stored is counted as lost (unconfirmed) and not resent")]
        public async Task Unconfirmed_records_are_counted_not_resent_Async()
        {
            var exporter = new CapturingExporter { Answer = AuditRecordOutcome.Unconfirmed };
            using var provider = Build(exporter);
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditExportMetrics>().Meter);
            var worker = provider.GetServices<IHostedService>().OfType<AuditExportWorker>().Single();
            await worker.StartAsync(default);

            await PersistAsync(provider, new AccessLog("action", "user"), Data());

            await EventuallyAsync(() => meter.Sum("juice.audit.export.records", ("outcome", "dropped"),
                ("reason", "unconfirmed")) == 1);
            await worker.StopAsync(default);
            exporter.Calls.Should().Be(1);
        }

        [Fact(DisplayName = "An attempt that stores some records starts the retry count over: a slow collector isn't given up on")]
        public async Task Progress_restarts_the_retry_count_Async()
        {
            // One record per call, the rest retryable, as an old collector working through a backlog in its time.
            var exporter = new CapturingExporter { StorePerCall = 1 };
            using var provider = Build(exporter, o =>
            {
                o.MaxRetryAttempts = 1;
                o.InitialBackoff = TimeSpan.FromMilliseconds(5);
                o.MaxBackoff = TimeSpan.FromMilliseconds(5);
            });
            using var meter = new MeterRecorder(provider.GetRequiredService<AuditExportMetrics>().Meter);
            for (var i = 0; i < 5; i++)
            {
                await PersistAsync(provider, new AccessLog("action", "user"));
            }
            var worker = provider.GetServices<IHostedService>().OfType<AuditExportWorker>().Single();
            await worker.StartAsync(default);

            await EventuallyAsync(() => meter.Sum("juice.audit.export.records", ("outcome", "exported")) == 5);
            await worker.StopAsync(default);
            meter.Sum("juice.audit.export.records", ("outcome", "dropped")).Should().Be(0,
                "5 attempts for one retry allowed, each storing a record");
            exporter.Calls.Should().Be(5);
        }

        private static async Task EventuallyAsync(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                watch.ElapsedMilliseconds.Should().BeLessThan(5000, "the condition should hold by then");
                await Task.Delay(10);
            }
        }

        [Theory(DisplayName = "A batch size or queue capacity of 0 is refused, not left to spin the worker")]
        [InlineData(0, 10)]
        [InlineData(-1, 10)]
        [InlineData(10, 0)]
        public void Empty_batches_are_refused(int maxBatchSize, int queueCapacity)
        {
            using var provider = Build(new CapturingExporter(), o =>
            {
                o.MaxBatchSize = maxBatchSize;
                o.QueueCapacity = queueCapacity;
            });

            var read = () => provider.GetRequiredService<IOptions<AuditExportOptions>>().Value;

            read.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Audit:Export:");
        }

        [Fact(DisplayName = "An access record sent over gRPC keeps the time the app made it")]
        public void Grpc_access_records_keep_their_time()
        {
            var made = DateTimeOffset.UtcNow.AddMinutes(-7);
            var access = new AccessLog("action", "user") { DateTime = made };

            var received = AuditMessageMapper.FromMessage(AuditMessageMapper.ToMessage(access, Array.Empty<DataAudit>()));

            received.AccessLog!.DateTime.Should().Be(made, "batching and retries delay a record by minutes");
            received.AccessLog!.Id.Should().Be(access.Id);
        }

        [Fact(DisplayName = "An outcome the collector didn't set, or one this client doesn't know, is never read as stored")]
        public void Unknown_outcomes_are_retryable()
        {
            AuditMessageMapper.FromMessage(Grpc.AuditRecordResult.Types.Outcome.Unspecified)
                .Should().Be(AuditRecordOutcome.Retryable);
            AuditMessageMapper.FromMessage((Grpc.AuditRecordResult.Types.Outcome)99)
                .Should().Be(AuditRecordOutcome.Retryable, "a later collector's new outcome");
            AuditMessageMapper.FromMessage(Grpc.AuditRecordResult.Types.Outcome.Stored)
                .Should().Be(AuditRecordOutcome.Stored);
            AuditMessageMapper.ToMessage(new AuditRecordResult(Guid.NewGuid(), AuditRecordKind.Access,
                AuditRecordOutcome.Stored)).Outcome.Should().Be(Grpc.AuditRecordResult.Types.Outcome.Stored);
        }

        [Fact(DisplayName = "A data change sent without a time is stored with the time it arrived, not refused with its record")]
        public void A_data_change_without_a_time_is_kept()
        {
            var message = AuditMessageMapper.ToMessage(Data());
            message.Datetime = null;

            var received = AuditMessageMapper.FromMessage(message, null);

            received.DateTime.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        }

        /// <summary>Stores what it is sent; refuses the data changes when asked.</summary>
        private sealed class CapturingExporter : IAuditExporter
        {
            private int _calls;
            public bool RejectData { get; init; }
            /// <summary>The outcome of every access record (and, unless <see cref="RejectData"/>, its changes).</summary>
            public AuditRecordOutcome Answer { get; init; } = AuditRecordOutcome.Stored;
            /// <summary>Answers only this many records per call, the rest retryable; all when 0.</summary>
            public int StorePerCall { get; init; }
            public List<AuditExportItem> Items { get; } = new();
            public int Calls => _calls;

            public Task<AuditBatchResult> ExportAsync(IReadOnlyList<AuditExportItem> batch,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                lock (Items)
                {
                    Items.AddRange(batch);
                }
                var results = new List<AuditRecordResult>();
                for (var i = 0; i < batch.Count; i++)
                {
                    var item = batch[i];
                    var answer = StorePerCall > 0 && i >= StorePerCall ? AuditRecordOutcome.Retryable : Answer;
                    results.Add(new(item.AccessRecord.Id, AuditRecordKind.Access, answer));
                    results.AddRange(item.BestEffortEntries.Select(e => new AuditRecordResult(e.Id, AuditRecordKind.Data,
                        RejectData ? AuditRecordOutcome.Rejected : answer, "refused")));
                }
                return Task.FromResult(new AuditBatchResult(results));
            }
        }
    }
}
