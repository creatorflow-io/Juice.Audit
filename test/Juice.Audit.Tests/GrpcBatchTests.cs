using System.Diagnostics;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using Juice.Audit.EF;
using Juice.EF.Extensions;
using Juice.XUnit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Juice.Audit.AspNetCore.Extensions;
using Juice.Audit.Commands;
using Juice.Audit.Domain;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.Export;
using Juice.Audit.Grpc;
using Juice.Audit.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuditBatchResult = Juice.Audit.Commands.AuditBatchResult;

namespace Juice.Audit.Tests
{
    /// <summary>
    /// The gRPC exporter against an in-memory collector (T-EXPORT-3): batches with their ids, the unary fallback for a
    /// collector before 10.7, and the deadline that keeps a hanging collector from holding the worker.
    /// </summary>
    public sealed class GrpcBatchTests
    {
        private static AccessLog Access() => new("action", "user");

        private static DataAudit Data()
            => new("user", DateTimeOffset.UtcNow, "Modified", "db", "dbo", "Orders", "{\"Id\":1}", "{}", "trace");

        private static async Task<(WebApplication App, GrpcChannel Channel)> StartAsync(
            Action<WebApplication> map, Action<IServiceCollection>? services = null)
        {
            // Not Development (other tests set it for the process): its build-time validation would ask for the
            // repositories of the single-record handler, which a real collector registers and these tests don't need.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Services.AddGrpc();
            builder.Services.AddMediatR();
            services?.Invoke(builder.Services);
            var app = builder.Build();
            map(app);
            await app.StartAsync();
            var channel = GrpcChannel.ForAddress(app.GetTestServer().BaseAddress,
                new GrpcChannelOptions { HttpHandler = app.GetTestServer().CreateHandler() });
            return (app, channel);
        }

        private static GrpcAuditExporter Exporter(GrpcChannel channel, CountingLogger? logger = null,
            TimeSpan? timeout = null, GrpcExportState? state = null)
            => new(new AuditGrpcService.AuditGrpcServiceClient(channel), state ?? new GrpcExportState(),
                Options.Create(new AuditExportOptions { ExportTimeout = timeout ?? TimeSpan.FromSeconds(10) }),
                logger ?? new CountingLogger());

        [Fact(DisplayName = "LogBatch stores a batch with the ids given in the app, and a replay is a duplicate")]
        public async Task LogBatch_round_trip_keeps_the_ids_Async()
        {
            var store = new Store();
            var (app, channel) = await StartAsync(a => a.MapAuditGrpcServer(), s =>
            {
                s.AddAuditCommandHandlers();
                s.AddSingleton<IAuditBatchStore>(store);
            });
            await using var _ = app;
            var access = Access();
            access.SetMetadata("SpanId", "abc");
            var data = Data();
            var batch = new[] { new AuditExportItem(access, new[] { data }), new AuditExportItem(Access(), Array.Empty<DataAudit>()) };

            var first = await Exporter(channel).ExportAsync(batch, default);
            var replay = await Exporter(channel).ExportAsync(batch, default);

            first.Results.Should().HaveCount(3).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Stored);
            first.Results.Single(r => r.Id == data.Id).Kind.Should().Be(AuditRecordKind.Data);
            replay.Results.Should().OnlyContain(r => r.Outcome == AuditRecordOutcome.Duplicate);
            store.AccessLogs.Select(a => a.Id).Should().BeEquivalentTo(batch.Select(i => i.AccessRecord.Id));
            store.DataAudits.Should().ContainSingle().Which.Id.Should().Be(data.Id);
            store.AccessLogs.Single(a => a.Id == access.Id).Metadata["SpanId"]!.ToString().Should().Be("abc",
                "the metadata travels too");
        }

        [Fact(DisplayName = "T-EXPORT-3: a collector without LogBatch gets one record per call, nothing is dropped, and it's logged once")]
        public async Task An_old_collector_gets_unary_calls_Async()
        {
            var old = new OldCollector();
            var (app, channel) = await StartAsync(a => a.MapGrpcService<OldCollector>(), s => s.AddSingleton(old));
            await using var _ = app;
            var logger = new CountingLogger();
            var state = new GrpcExportState();
            var batch = new[] { new AuditExportItem(Access(), new[] { Data() }), new AuditExportItem(Access(), Array.Empty<DataAudit>()) };

            var first = await Exporter(channel, logger, state: state).ExportAsync(batch, default);
            var second = await Exporter(channel, logger, state: state).ExportAsync(batch, default);

            first.Results.Should().HaveCount(3).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Stored);
            second.AllStored.Should().BeTrue();
            old.Received.Should().HaveCount(4, "two records, sent twice, one per call");
            old.Received[0].AuditEntries.Should().ContainSingle();
            logger.Warnings.Should().Be(1, "the switch is logged once for the process");
            state.UseUnary.Should().BeTrue();
        }

        [Fact(DisplayName = "A collector that refuses the key: the batch fails for a retry, with one warning a minute")]
        public async Task A_refused_key_is_logged_and_retried_Async()
        {
            var (app, channel) = await StartAsync(a => a.MapGrpcService<KeyedCollector>());
            await using var _ = app;
            var logger = new CountingLogger();
            var state = new GrpcExportState();
            var batch = new[] { new AuditExportItem(Access(), Array.Empty<DataAudit>()) };

            for (var i = 0; i < 3; i++)
            {
                var export = () => Exporter(channel, logger, state: state).ExportAsync(batch, default);
                (await export.Should().ThrowAsync<RpcException>()).Which.StatusCode
                    .Should().Be(StatusCode.Unauthenticated, "the worker retries it like any failed call");
            }
            logger.Warnings.Should().Be(1, "the refusal is logged once a minute, not once per attempt");
            state.UseUnary.Should().BeFalse();
        }

        [Fact(DisplayName = "One record per call: a refused key is warned about, and the record it was sent is not resent")]
        public async Task A_refused_key_one_by_one_is_warned_Async()
        {
            var (app, channel) = await StartAsync(a => a.MapGrpcService<KeyedCollector>());
            await using var _ = app;
            var logger = new CountingLogger();
            var state = new GrpcExportState();
            state.SwitchToUnary(); // as after an old instance answered Unimplemented
            var batch = new[] { new AuditExportItem(Access(), new[] { Data() }), new AuditExportItem(Access(), Array.Empty<DataAudit>()) };

            var result = await Exporter(channel, logger, state: state).ExportAsync(batch, default);

            // A collector before 10.7 may refuse after storing the data changes: as any failed call, the record sent
            // is not resent, and the rest waits for the next attempt.
            result.Results.Take(2).Should().OnlyContain(r => r.Outcome == AuditRecordOutcome.Unconfirmed);
            result.Results.Skip(2).Should().ContainSingle().Which.Outcome.Should().Be(AuditRecordOutcome.Retryable);
            logger.Warnings.Should().Be(1, "the refusal is said, even on this path");
        }

        [Fact(DisplayName = "One record per call: the collector's key check refusal leaves every record retryable")]
        public async Task A_key_check_refusal_one_by_one_is_retryable_Async()
        {
            var (app, channel) = await StartAsync(a => a.MapGrpcService<KeyCheckingCollector>());
            await using var _ = app;
            var logger = new CountingLogger();
            var state = new GrpcExportState();
            state.SwitchToUnary();
            var batch = new[] { new AuditExportItem(Access(), new[] { Data() }), new AuditExportItem(Access(), Array.Empty<DataAudit>()) };

            var result = await Exporter(channel, logger, state: state).ExportAsync(batch, default);

            result.Results.Should().HaveCount(3).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Retryable,
                "the key check refused the call before the service ran: nothing was stored");
            logger.Warnings.Should().Be(1);
        }

        /// <summary>Refuses as the collector's key check does: with the trailer that says nothing was stored.</summary>
        private sealed class KeyCheckingCollector : AuditGrpcService.AuditGrpcServiceBase
        {
            private static RpcException Refused() => new(
                new Status(StatusCode.Unauthenticated, "The x-api-key header is missing."),
                new Metadata { { AuditGrpcClientOptionsExtensions.KeyRefusedTrailer, AuditGrpcClientOptionsExtensions.KeyRefusedValue } });

            public override Task<Juice.Audit.Grpc.AuditBatchResult> LogBatch(AuditBatchRequest request,
                ServerCallContext context) => throw Refused();

            public override Task<AuditOperationResult> Log(AuditRequest request, ServerCallContext context)
                => throw Refused();
        }

        [Theory(DisplayName = "AddAuditApiKey refuses a key that can't travel as a header")]
        [InlineData("")]
        [InlineData(" key")]
        [InlineData("key ")]
        [InlineData("s3crét")]
        [InlineData("a\tb")]
        public void An_unusable_key_is_refused_at_configuration(string key)
        {
            var configure = () => new global::Grpc.Net.ClientFactory.GrpcClientFactoryOptions().AddAuditApiKey(key);
            configure.Should().Throw<ArgumentException>().Which.Message.Should().Contain("printable ASCII");
            AuditGrpcClientOptionsExtensions.IsValidApiKey("local-collector-key_2+/=").Should().BeTrue();
        }

        /// <summary>A collector whose key the app doesn't have: it refuses every call before the service runs.</summary>
        private sealed class KeyedCollector : AuditGrpcService.AuditGrpcServiceBase
        {
            private static RpcException Refused() =>
                new(new Status(StatusCode.Unauthenticated, "The x-api-key header is missing."));

            public override Task<Juice.Audit.Grpc.AuditBatchResult> LogBatch(AuditBatchRequest request,
                ServerCallContext context) => throw Refused();

            public override Task<AuditOperationResult> Log(AuditRequest request, ServerCallContext context)
                => throw Refused();
        }

        [Fact(DisplayName = "A hanging collector is cut off at ExportTimeout")]
        public async Task A_hanging_collector_is_cut_off_at_the_deadline_Async()
        {
            var (app, channel) = await StartAsync(a => a.MapGrpcService<HangingCollector>());
            await using var _ = app;
            var watch = Stopwatch.StartNew();

            var export = () => Exporter(channel, timeout: TimeSpan.FromMilliseconds(300))
                .ExportAsync(new[] { new AuditExportItem(Access(), Array.Empty<DataAudit>()) }, default);

            // DeadlineExceeded on a real channel; the in-memory test server may report the reset differently. Either
            // way the call fails, in time, and the worker retries it.
            await export.Should().ThrowAsync<RpcException>();
            watch.ElapsedMilliseconds.Should().BeLessThan(3000);
        }

        private static GrpcExportState Unary()
        {
            var state = new GrpcExportState();
            state.SwitchToUnary();
            return state;
        }

        private static List<AuditExportItem> Items(int count, bool withData = false)
            => Enumerable.Range(0, count)
                .Select(_ => new AuditExportItem(Access(), withData ? new[] { Data() } : Array.Empty<DataAudit>()))
                .ToList();

        [Fact(DisplayName = "Unary fallback: a call cut off may have stored its record, so it isn't resent; the rest is retryable")]
        public async Task A_call_cut_off_is_unconfirmed_Async()
        {
            var (app, channel) = await StartAsync(a => a.MapGrpcService<HangingCollector>());
            await using var _ = app;
            var watch = Stopwatch.StartNew();

            var result = await Exporter(channel, timeout: TimeSpan.FromMilliseconds(300), state: Unary())
                .ExportAsync(Items(5), default);

            watch.ElapsedMilliseconds.Should().BeLessThan(3000, "one call is cut off, and no more start");
            result.Results.Select(r => r.Outcome).Should().Equal(AuditRecordOutcome.Unconfirmed,
                AuditRecordOutcome.Retryable, AuditRecordOutcome.Retryable, AuditRecordOutcome.Retryable,
                AuditRecordOutcome.Retryable);
        }

        [Fact(DisplayName = "Unary fallback: a record the old collector fails is refused, with its data changes, never resent")]
        public async Task An_old_collector_failure_is_rejected_Async()
        {
            // Before 10.7 the collector saves the data changes, then the access record; when that fails, a resend would
            // store the data changes again, under new ids.
            var old = new FailingOldCollector();
            var (app, channel) = await StartAsync(a => a.MapGrpcService<FailingOldCollector>(), s => s.AddSingleton(old));
            await using var _ = app;

            var result = await Exporter(channel, state: Unary()).ExportAsync(Items(2, withData: true), default);

            result.Results.Should().HaveCount(4).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Rejected
                && r.Reason == "String or binary data would be truncated.");
            old.Calls.Should().Be(2, "each record is sent once");
        }

        [Fact(DisplayName = "Unary fallback: a collector that can't be reached never got the records: all are retryable")]
        public async Task An_unreachable_collector_is_retried_Async()
        {
            // Nothing listens on port 1.
            using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1");

            var result = await Exporter(channel, state: Unary()).ExportAsync(Items(3, withData: true), default);

            result.Results.Should().HaveCount(6).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Retryable);
        }

        [Fact(DisplayName = "Unary fallback: an unexpected error keeps the answers of the records already stored, so they aren't resent")]
        public async Task An_unexpected_error_keeps_the_answers_so_far_Async()
        {
            var old = new OldCollector();
            var (app, channel) = await StartAsync(a => a.MapGrpcService<OldCollector>(), s => s.AddSingleton(old));
            await using var _ = app;
            // The second call fails in the client, outside the transport: not an RpcException.
            var client = new AuditGrpcService.AuditGrpcServiceClient(channel.Intercept(new FailNthCall(2)));
            var exporter = new GrpcAuditExporter(client, Unary(), Options.Create(new AuditExportOptions()),
                new CountingLogger());

            var result = await exporter.ExportAsync(Items(3), default);

            result.Results.Select(r => r.Outcome).Should().Equal(AuditRecordOutcome.Stored,
                AuditRecordOutcome.Unconfirmed, AuditRecordOutcome.Retryable);
            old.Received.Should().HaveCount(1);
        }

        [Fact(DisplayName = "After falling back to one record per call, LogBatch is tried again a while later")]
        public async Task LogBatch_is_tried_again_later_Async()
        {
            var collector = new UpgradingCollector();
            var (app, channel) = await StartAsync(a => a.MapGrpcService<UpgradingCollector>(),
                s => s.AddSingleton(collector));
            await using var _ = app;
            var state = new GrpcExportState(TimeSpan.FromMilliseconds(300));
            var logger = new CountingLogger();

            await Exporter(channel, logger, state: state).ExportAsync(Items(1), default);
            // The old instance is replaced, as in a rolling upgrade.
            collector.Upgraded = true;
            await Exporter(channel, logger, state: state).ExportAsync(Items(1), default);
            collector.Unary.Should().Be(2, "within the window records still go one by one");
            await Task.Delay(400);
            var later = await Exporter(channel, logger, state: state).ExportAsync(Items(1), default);

            later.AllStored.Should().BeTrue();
            collector.Batches.Should().Be(1, "LogBatch is used again");
            collector.Unary.Should().Be(2);
            logger.Warnings.Should().Be(1);
        }

        [Fact(DisplayName = "Unary fallback: a slow collector is never cut off mid-write; calls stop when the batch's time is spent")]
        public async Task A_slow_collector_is_not_cut_off_Async()
        {
            var slow = new SlowOldCollector(TimeSpan.FromMilliseconds(150));
            var (app, channel) = await StartAsync(a => a.MapGrpcService<SlowOldCollector>(), s => s.AddSingleton(slow));
            await using var _ = app;

            // 10 records of 150 ms in a 400 ms batch: before 10.7 the call in flight at 400 ms was cut off.
            var result = await Exporter(channel, timeout: TimeSpan.FromMilliseconds(400), state: Unary())
                .ExportAsync(Items(10), default);

            var stored = result.Results.Count(r => r.Outcome == AuditRecordOutcome.Stored);
            stored.Should().BeInRange(2, 4);
            result.Results.Should().NotContain(r => r.Outcome == AuditRecordOutcome.Unconfirmed);
            result.Results.Skip(stored).Should().OnlyContain(r => r.Outcome == AuditRecordOutcome.Retryable,
                "the records after the last call wait for the next attempt");
            slow.Completed.Should().Be(stored, "every record sent was answered");
        }

        [Theory(DisplayName = "R11: a collector under UseAudit() records no access log for the batches it is sent")]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_collector_does_not_audit_itself_Async(bool defaultRules)
        {
            var store = new Store();
            var audited = new CapturingAuditService();
            var (app, channel) = await StartAsync(a =>
            {
                // The control case drops the default rules: the same POST is then recorded, so the test can fail.
                a.UseAudit("collector", o =>
                {
                    if (!defaultRules)
                    {
                        o.Clear().Include(string.Empty, "POST");
                    }
                });
                a.MapAuditGrpcServer();
            }, s =>
            {
                s.AddAuditCommandHandlers();
                s.AddSingleton<IAuditBatchStore>(store);
                s.AddAuditContextAccessor();
                s.AddSingleton<IAuditService>(audited);
            });
            await using var _ = app;

            var result = await Exporter(channel).ExportAsync(
                new[] { new AuditExportItem(Access(), new[] { Data() }) }, default);

            result.AllStored.Should().BeTrue();
            store.AccessLogs.Should().ContainSingle("only the record sent is stored");
            if (defaultRules)
            {
                audited.Records.Should().BeEmpty("the collector's own gRPC path is excluded by default");
            }
            else
            {
                audited.Records.Should().ContainSingle().Which.Request!.Path.Should()
                    .Be("/juice.audit.grpc.AuditGrpcService/LogBatch");
            }
        }

        [IgnoreOnCITheory(DisplayName = "A LogBatch with records of two tenants stores each under its tenant; each tenant reads its own, the root both")]
        [InlineData("SqlServer")]
        [InlineData("PostgreSQL")]
        public async Task A_mixed_tenant_batch_keeps_each_tenant_apart_Async(string provider)
        {
            var tenants = new MultiTenantDbContextTests.StubMultiTenantContextAccessor();
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var (app, channel) = await StartAsync(a => a.MapAuditGrpcServer(), s =>
            {
                s.AddSingleton<IMultiTenantContextAccessor>(tenants);
                s.AddAuditCommandHandlers()
                    .AddAuditDbContext(configuration, o => o.DatabaseProvider = provider)
                    .AddEFAuditRepos();
            });
            await using var _ = app;
            using (var scope = app.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AuditDbContext>().MigrateAsync();
            }
            var traceId = Guid.NewGuid().ToString("N");
            AccessLog Tenant(string id)
            {
                var access = new AccessLog("tenant_batch", "xunit", id);
                access.SetRequestInfo(new RequestInfo("POST", "/orders", null, null, null, "https", null, traceId, "localhost"));
                return access;
            }
            var batch = new[]
            {
                new AuditExportItem(Tenant("tenant-a"), new[] { new DataAudit("xunit", DateTimeOffset.UtcNow, "Inserted",
                    "db", "dbo", "Orders", "{}", "{}", traceId, "tenant-a") }),
                new AuditExportItem(Tenant("tenant-b"), new[] { new DataAudit("xunit", DateTimeOffset.UtcNow, "Inserted",
                    "db", "dbo", "Orders", "{}", "{}", traceId, "tenant-b") })
            };

            var result = await Exporter(channel).ExportAsync(batch, default);

            result.AllStored.Should().BeTrue(result.ToOperationResult().ToString());
            async Task<(string?[] Access, string?[] Data)> ReadAsync(string? tenant)
            {
                tenants.SetTenant(tenant);
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
                return (await db.AccessLogs.Where(a => a.Request!.TraceId == traceId).Select(a => a.TenantId).ToArrayAsync(),
                    await db.AuditEntries.Where(d => d.TraceId == traceId).Select(d => d.TenantId).ToArrayAsync());
            }
            var a = await ReadAsync("tenant-a");
            a.Access.Should().Equal("tenant-a");
            a.Data.Should().Equal("tenant-a");
            var root = await ReadAsync(null);
            root.Access.Should().BeEquivalentTo(new[] { "tenant-a", "tenant-b" });
            root.Data.Should().BeEquivalentTo(new[] { "tenant-a", "tenant-b" });
        }

        private sealed class CapturingAuditService : IAuditService
        {
            public List<AccessLog> Records { get; } = new();

            public Task<IOperationResult> PersistAuditInformationAsync(AccessLog accessLog, DataAudit[] auditEntries,
                CancellationToken token)
            {
                lock (Records)
                {
                    Records.Add(accessLog);
                }
                return Task.FromResult<IOperationResult>(OperationResult.Success);
            }
        }

        /// <summary>A collector before 10.7: it only has the unary Log.</summary>
        private sealed class OldCollector : AuditGrpcService.AuditGrpcServiceBase
        {
            public List<AuditRequest> Received { get; } = new();

            public override Task<AuditOperationResult> Log(AuditRequest request, ServerCallContext context)
            {
                lock (Received)
                {
                    Received.Add(request);
                }
                return Task.FromResult(new AuditOperationResult { Succeeded = true });
            }
        }

        /// <summary>A collector before 10.7 whose database refuses the access record, after the data changes.</summary>
        private sealed class FailingOldCollector : AuditGrpcService.AuditGrpcServiceBase
        {
            private int _calls;
            public int Calls => _calls;

            public override Task<AuditOperationResult> Log(AuditRequest request, ServerCallContext context)
            {
                Interlocked.Increment(ref _calls);
                return Task.FromResult(new AuditOperationResult
                {
                    Succeeded = false,
                    Message = "String or binary data would be truncated."
                });
            }
        }

        /// <summary>Has no LogBatch until <see cref="Upgraded"/>; counts the calls of each kind.</summary>
        private sealed class UpgradingCollector : AuditGrpcService.AuditGrpcServiceBase
        {
            private int _batches, _unary;
            public bool Upgraded { get; set; }
            public int Batches => _batches;
            public int Unary => _unary;

            public override Task<Juice.Audit.Grpc.AuditBatchResult> LogBatch(AuditBatchRequest request,
                ServerCallContext context)
            {
                if (!Upgraded)
                {
                    throw new RpcException(new Status(StatusCode.Unimplemented, "no LogBatch"));
                }
                Interlocked.Increment(ref _batches);
                var reply = new Juice.Audit.Grpc.AuditBatchResult();
                reply.Results.AddRange(request.Items.Select(i => new Juice.Audit.Grpc.AuditRecordResult
                {
                    Id = i.AccessLog.Id,
                    Outcome = Juice.Audit.Grpc.AuditRecordResult.Types.Outcome.Stored
                }));
                return Task.FromResult(reply);
            }

            public override Task<AuditOperationResult> Log(AuditRequest request, ServerCallContext context)
            {
                Interlocked.Increment(ref _unary);
                return Task.FromResult(new AuditOperationResult { Succeeded = true });
            }
        }

        /// <summary>Fails the <c>n</c>th call in the client, with an exception that isn't the transport's.</summary>
        private sealed class FailNthCall(int n) : Interceptor
        {
            private int _calls;

            public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request,
                ClientInterceptorContext<TRequest, TResponse> context,
                AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
                => Interlocked.Increment(ref _calls) == n
                    ? throw new InvalidOperationException("channel disposed")
                    : continuation(request, context);
        }

        /// <summary>A collector before 10.7 that takes a while per record, as under a backlog.</summary>
        private sealed class SlowOldCollector(TimeSpan delay) : AuditGrpcService.AuditGrpcServiceBase
        {
            private int _completed;
            public int Completed => _completed;

            public override async Task<AuditOperationResult> Log(AuditRequest request, ServerCallContext context)
            {
                await Task.Delay(delay);
                Interlocked.Increment(ref _completed);
                return new AuditOperationResult { Succeeded = true };
            }
        }

        /// <summary>Never answers; when the call is cancelled it just goes away, as a stuck server would.</summary>
        private sealed class HangingCollector : AuditGrpcService.AuditGrpcServiceBase
        {
            public override async Task<Juice.Audit.Grpc.AuditBatchResult> LogBatch(AuditBatchRequest request,
                ServerCallContext context)
            {
                await HangAsync(context);
                return new();
            }

            public override async Task<AuditOperationResult> Log(AuditRequest request, ServerCallContext context)
            {
                await HangAsync(context);
                return new();
            }

            private static async Task HangAsync(ServerCallContext context)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private sealed class Store : IAuditBatchStore
        {
            private readonly List<object> _tracked = new();
            public List<AccessLog> AccessLogs { get; } = new();
            public List<DataAudit> DataAudits { get; } = new();

            public Task<IReadOnlyCollection<Guid>> GetExistingAccessLogIdsAsync(IReadOnlyCollection<Guid> ids,
                CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyCollection<Guid>>(ids.Intersect(AccessLogs.Select(a => a.Id)).ToList());

            public Task<IReadOnlyCollection<Guid>> GetExistingDataAuditIdsAsync(IReadOnlyCollection<Guid> ids,
                CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyCollection<Guid>>(ids.Intersect(DataAudits.Select(d => d.Id)).ToList());

            public void Add(AccessLog accessLog) => _tracked.Add(accessLog);
            public void Add(DataAudit dataAudit) => _tracked.Add(dataAudit);

            public Task SaveChangesAsync(CancellationToken cancellationToken)
            {
                AccessLogs.AddRange(_tracked.OfType<AccessLog>());
                DataAudits.AddRange(_tracked.OfType<DataAudit>());
                _tracked.Clear();
                return Task.CompletedTask;
            }

            public void ClearTracked() => _tracked.Clear();
        }

        private sealed class CountingLogger : ILogger<GrpcAuditExporter>
        {
            private int _warnings;
            public int Warnings => _warnings;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    Interlocked.Increment(ref _warnings);
                }
            }
        }
    }
}
