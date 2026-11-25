using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Juice.Audit.Api.Query;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Grpc;
using Juice.Audit.Services;
using Juice.XUnit;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Tests
{
    /// <summary>
    /// End to end against the collector image (<c>deploy/collector/docker-compose.yml</c>): the sample app runs as its
    /// own process in remote mode, sends access records over gRPC to the container and data changes through RabbitMQ,
    /// and the records are read back through the collector's read API. Start the stack first:
    /// <code>docker compose -f deploy/collector/docker-compose.yml up -d --build --wait</code>
    /// The tests skip themselves when the collector isn't up.
    /// </summary>
    [Collection(nameof(CollectorContainerTests))]
    public sealed class CollectorContainerTests : IClassFixture<CollectorContainerTests.SampleApp>
    {
        public const string GrpcAddress = "http://127.0.0.1:7290";
        public const string HttpAddress = "http://127.0.0.1:7291";
        public const string Container = "juice-audit-collector";

        // The compose file's own overrides, with its defaults.
        public static readonly string ApiKey = Env("AUDIT_COLLECTOR_KEY", "local-collector-key");
        public static readonly string DbPassword = Env("AUDIT_DB_PASSWORD", "audit-local");

        private static string Env(string name, string fallback)
            => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

        private readonly SampleApp _app;

        public CollectorContainerTests(SampleApp app) => _app = app;

        [IgnoreOnCIFact(DisplayName = "Container: a committed change and its access record reach the collector")]
        public async Task A_committed_change_reaches_the_collector_Async()
        {
            _app.SkipUnlessRunning();
            var (response, traceId) = await _app.PlaceOrderAsync();
            response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());

            var trace = await EventuallyAsync(traceId, t => t.AccessLogs.Count == 1 && t.DataChanges.Count == 1);

            var access = trace.AccessLogs.Single();
            access.Request!.Method.Should().Be("POST");
            access.Server!.App.Should().Be("XUnitTest");
            access.Metadata["OutboxedChanges"].Should().Be("1", "the change went through the outbox and RabbitMQ");
            access.Metadata.Should().NotContainKey("BestEffortChanges");
            var change = trace.DataChanges.Single();
            change.Action.Should().Be("Inserted");
            change.Table.Should().Be("Orders");
        }

        [IgnoreOnCIFact(DisplayName = "Container: a rolled back change leaves only the record of the attempt")]
        public async Task A_rollback_leaves_no_data_change_Async()
        {
            _app.SkipUnlessRunning();
            var (failed, traceId) = await _app.PlaceOrderAsync(fail: true);
            ((int)failed.StatusCode).Should().Be(409);

            // A committed order placed after it: once its change is in, the rolled back one would have been too.
            var (after, afterTrace) = await _app.PlaceOrderAsync();
            after.IsSuccessStatusCode.Should().BeTrue();
            await EventuallyAsync(afterTrace, t => t.DataChanges.Count == 1);

            var trace = await EventuallyAsync(traceId, t => t.AccessLogs.Count == 1);
            trace.DataChanges.Should().BeEmpty();
            trace.AccessLogs.Single().Metadata["UncommittedChanges"].Should().Be("1");
        }

        [IgnoreOnCIFact(DisplayName = "Container: credentials never reach storage, from the app or sent as-is")]
        public async Task Credentials_are_not_stored_Async()
        {
            _app.SkipUnlessRunning();

            // The app records only the headers UseAudit() allows, and Authorization isn't one of them by default.
            var (response, appTrace) = await _app.PlaceOrderAsync(authorization: "Bearer app-secret-token");
            response.IsSuccessStatusCode.Should().BeTrue();
            var fromApp = (await EventuallyAsync(appTrace, t => t.AccessLogs.Count == 1)).AccessLogs.Single();
            fromApp.Request!.Headers.Should().NotContain("app-secret-token");

            // An app that stores it anyway (StoreRequestHeaders): the collector masks it before writing.
            var traceId = ActivityTraceId.CreateRandom().ToHexString();
            var access = new AccessLog("container_e2e_redaction", "xunit");
            access.SetRequestInfo(new RequestInfo("GET", "/e2e", null, null,
                "{\"Authorization\":[\"Bearer sent-secret-token\"],\"Accept\":[\"*/*\"]}", "http", null, traceId, "localhost"));
            await SendAsync(access);

            var stored = (await EventuallyAsync(traceId, t => t.AccessLogs.Count == 1)).AccessLogs.Single();
            stored.Request!.Headers.Should().NotContain("sent-secret-token").And.Contain("***").And.Contain("*/*");
            stored.Metadata["Redacted"].Should().Contain("Authorization");
        }

        private static async Task SendAsync(AccessLog access)
        {
            using var channel = GrpcChannel.ForAddress(GrpcAddress);
            var reply = await new AuditGrpcService.AuditGrpcServiceClient(channel).LogBatchAsync(
                new AuditBatchRequest { Items = { AuditMessageMapper.ToMessage(access, Array.Empty<Domain.DataAuditAggregate.DataAudit>()) } },
                new Metadata { { "x-api-key", ApiKey } });
            reply.Results.Single().Outcome.Should().Be(AuditRecordResult.Types.Outcome.Stored);
        }

        [IgnoreOnCIFact(DisplayName = "Container: the read API answers on the HTTP port only, not on the gRPC port")]
        public async Task The_read_api_is_not_on_the_grpc_port_Async()
        {
            _app.SkipUnlessRunning();
            // The gRPC port speaks HTTP/2 without TLS: ask with HTTP/2 from the first byte.
            using var http = new HttpClient
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            (await http.GetAsync($"{GrpcAddress}/api/audit/overview")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var plain = new HttpClient();
            (await plain.GetAsync($"{HttpAddress}/api/audit/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [IgnoreOnCIFact(DisplayName = "Container: gRPC needs the API key, and a record sent twice is stored once")]
        public async Task Grpc_requires_the_key_and_deduplicates_Async()
        {
            _app.SkipUnlessRunning();
            using var channel = GrpcChannel.ForAddress(GrpcAddress);
            var client = new AuditGrpcService.AuditGrpcServiceClient(channel);
            var traceId = ActivityTraceId.CreateRandom().ToHexString();
            var access = new AccessLog("container_e2e", "xunit");
            access.SetRequestInfo(new RequestInfo("POST", "/e2e", null, null, null, "http", null, traceId, "localhost"));
            var request = new AuditBatchRequest { Items = { AuditMessageMapper.ToMessage(access, Array.Empty<Domain.DataAuditAggregate.DataAudit>()) } };

            var noKey = async () => await client.LogBatchAsync(request);
            var refused = (await noKey.Should().ThrowAsync<RpcException>()).Which;
            refused.StatusCode.Should().Be(StatusCode.Unauthenticated);
            AuditGrpcClientOptionsExtensions.IsKeyRefusal(refused).Should().BeTrue(
                "the trailer that tells the app nothing was stored reaches it");
            var wrongKey = async () => await client.LogBatchAsync(request, new Metadata { { "x-api-key", "wrong" } });
            (await wrongKey.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);

            var key = new Metadata { { "x-api-key", ApiKey } };
            (await client.LogBatchAsync(request, key)).Results.Single().Outcome
                .Should().Be(AuditRecordResult.Types.Outcome.Stored);
            (await client.LogBatchAsync(request, key)).Results.Single().Outcome
                .Should().Be(AuditRecordResult.Types.Outcome.Duplicate);
            (await EventuallyAsync(traceId, t => t.AccessLogs.Count == 1)).AccessLogs.Single().Id.Should().Be(access.Id);
        }

        [IgnoreOnCIFact(DisplayName = "Container: records made while the collector is down arrive once it is back")]
        public async Task Records_survive_a_collector_restart_Async()
        {
            _app.SkipUnlessRunning();
            Docker("stop", Container);
            try
            {
                var (response, traceId) = await _app.PlaceOrderAsync();
                response.IsSuccessStatusCode.Should().BeTrue("the app doesn't wait on the collector");
                await Task.Delay(TimeSpan.FromSeconds(2));
                Docker("start", Container);
                await SampleApp.WaitForCollectorAsync(TimeSpan.FromSeconds(60));

                // The change waited in RabbitMQ; the access record in the app's export queue, retried with backoff.
                var trace = await EventuallyAsync(traceId, t => t.AccessLogs.Count == 1 && t.DataChanges.Count == 1,
                    timeoutSeconds: 60);
                trace.AccessLogs.Single().Metadata["OutboxedChanges"].Should().Be("1");
            }
            finally
            {
                Docker("start", Container);
                await SampleApp.WaitForCollectorAsync(TimeSpan.FromSeconds(60));
            }
        }

        private static async Task<AuditTrace> EventuallyAsync(string traceId, Func<AuditTrace, bool> condition,
            int timeoutSeconds = 30)
        {
            using var http = new HttpClient { BaseAddress = new Uri(HttpAddress) };
            var watch = Stopwatch.StartNew();
            AuditTrace? trace = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                var response = await http.GetAsync($"/api/audit/traces/{traceId}");
                if (response.IsSuccessStatusCode)
                {
                    trace = await response.Content.ReadFromJsonAsync<AuditTrace>();
                    if (trace != null && condition(trace))
                    {
                        return trace;
                    }
                }
                await Task.Delay(250);
            }
            throw new Xunit.Sdk.XunitException($"Trace {traceId} didn't reach the expected state in {timeoutSeconds} s: "
                + $"{trace?.AccessLogs.Count ?? 0} access record(s), {trace?.DataChanges.Count ?? 0} data change(s).");
        }

        private static void Docker(params string[] args)
        {
            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start)!;
            // Read both streams while it runs, so a full pipe can't stall it.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"docker {string.Join(' ', args)} didn't finish in 60 s.");
            }
            process.ExitCode.Should().Be(0, error.GetAwaiter().GetResult() + output.GetAwaiter().GetResult());
        }

        /// <summary>
        /// <c>Juice.Audit.Tests.Host</c> as its own process, in remote mode against the collector stack: access records
        /// over gRPC with the stack's key, data changes through its RabbitMQ, the time store in its PostgreSQL.
        /// </summary>
        public sealed class SampleApp : IAsyncLifetime
        {
            private Process? _process;
            private readonly System.Text.StringBuilder _output = new();
            private HttpClient? _http;
            private string? _skip;

            public async ValueTask InitializeAsync()
            {
                if (!await WaitForCollectorAsync(TimeSpan.FromSeconds(2)))
                {
                    _skip = "The collector stack isn't running: docker compose -f deploy/collector/docker-compose.yml up -d --build --wait";
                    return;
                }

                var hostDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..",
                    "Juice.Audit.Tests.Host", new DirectoryInfo(AppContext.BaseDirectory).Name));
                var port = FreePort();
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = hostDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(Path.Combine(hostDir, "Juice.Audit.Tests.Host.dll"));
                var env = start.Environment;
                env["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
                env["ASPNETCORE_ENVIRONMENT"] = "Production";
                env["Audit__Mode"] = "Remote";
                env["Audit__Grpc__Address"] = GrpcAddress;
                env["Audit__Grpc__ApiKey"] = ApiKey;
                env["Audit__Export__Synchronous"] = "false";
                env["Audit__Export__MaxBatchAge"] = "00:00:00.200";
                env["Audit__RabbitMQ__Host"] = "127.0.0.1";
                env["Audit__RabbitMQ__Port"] = "5682";
                env["Audit__RabbitMQ__UserName"] = "guest";
                env["Audit__RabbitMQ__Password"] = "guest";
                // juice.audit to RabbitMQ instead of the in-process consumer.
                env["Messaging__PublishingPolicies__Rules__0__Publishers__0__Key"] = "rabbitmq";
                env["Measurement__DatabaseProvider"] = "PostgreSQL";
                env["ConnectionStrings__PostgreConnection"] =
                    $"Host=127.0.0.1;Port=5442;Database=sample;Username=audit;Password={DbPassword}";

                _process = Process.Start(start)!;
                _process.OutputDataReceived += (_, e) => { lock (_output) { _output.AppendLine(e.Data); } };
                _process.ErrorDataReceived += (_, e) => { lock (_output) { _output.AppendLine(e.Data); } };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    if (_process.HasExited)
                    {
                        throw new InvalidOperationException("The sample app stopped:\n" + Output);
                    }
                    try
                    {
                        if ((await _http.GetAsync("/app")).IsSuccessStatusCode)
                        {
                            return;
                        }
                    }
                    catch (HttpRequestException) { }
                    if (watch.Elapsed > TimeSpan.FromSeconds(60))
                    {
                        // Not left running if the fixture isn't disposed after a failed start.
                        _process.Kill(entireProcessTree: true);
                        throw new TimeoutException("The sample app didn't start in 60 s:\n" + Output);
                    }
                    await Task.Delay(250);
                }
            }

            public string Output { get { lock (_output) { return _output.ToString(); } } }

            public void SkipUnlessRunning()
            {
                if (_skip != null)
                {
                    Assert.Skip(_skip);
                }
            }

            public async Task<(HttpResponseMessage Response, string TraceId)> PlaceOrderAsync(bool fail = false,
                string? authorization = null)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/orders?number=SO-{Guid.NewGuid():N}&fail={fail}");
                if (authorization != null)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", authorization);
                }
                var response = await _http!.SendAsync(request);
                return (response, response.Headers.GetValues("X-Trace-Id").Single());
            }

            public static async Task<bool> WaitForCollectorAsync(TimeSpan timeout)
            {
                using var http = new HttpClient { BaseAddress = new Uri(HttpAddress), Timeout = TimeSpan.FromSeconds(2) };
                var watch = Stopwatch.StartNew();
                do
                {
                    try
                    {
                        if ((await http.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK)
                        {
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
                    await Task.Delay(250);
                } while (watch.Elapsed < timeout);
                return false;
            }

            private static int FreePort()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }

            public ValueTask DisposeAsync()
            {
                _http?.Dispose();
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(10_000);
                }
                _process?.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
