using FluentAssertions;
using Juice.Audit.Collector;
using Juice.Audit.Commands;
using Juice.Audit.Domain;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.MediatR;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// Records are processed in the collector before storage (US5): the configured headers are masked, long bodies
    /// and changes are cut, processors run in order, and one that fails or refuses affects only its record.
    /// </summary>
    public sealed class CollectorProcessorTests
    {
        private static (ServiceProvider Provider, MemoryAuditStore Store) Build(Action<AuditCollectorBuilder>? add = null,
            int maxBodyLength = 16)
        {
            var store = new MemoryAuditStore();
            var services = new ServiceCollection();
            services.AddMediatR();
            services.AddSingleton<IAuditBatchStore>(store);
            services.AddSingleton<ProcessorLog>();
            services.AddAuditCommandHandlers();
            var collector = services.AddAuditCollector(o => o.MaxBodyLength = maxBodyLength);
            add?.Invoke(collector);
            return (services.BuildServiceProvider(), store);
        }

        private static async Task<AuditBatchResult> SaveAsync(IServiceProvider provider, params AuditBatchItem[] items)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IRequestHandler<SaveAuditBatchCommand, AuditBatchResult>>()
                .Handle(new SaveAuditBatchCommand(items), default);
        }

        private static AccessLog Access(string action = "action", string? requestHeaders = null, string? body = null,
            string? responseHeaders = null, string? responseBody = null, string? error = null)
        {
            var access = new AccessLog(action, "user");
            access.SetRequestInfo(new RequestInfo("POST", "/orders", body, null, requestHeaders, "https", null, "trace",
                "localhost"));
            access.UpdateResponseInfo(r =>
            {
                r.SetResponseInfo(200, responseHeaders ?? "{}", 5);
                if (responseBody != null)
                {
                    r.TrySetData(responseBody);
                }
                if (error != null)
                {
                    r.TrySetError(error);
                }
            });
            return access;
        }

        private static DataAudit Data(string changes = "{}")
            => new("user", DateTimeOffset.UtcNow, "Modified", null, null, "Orders", "{}", changes, "trace");

        [Fact(DisplayName = "The configured headers are masked in the request and response headers, ignoring case")]
        public async Task Headers_are_redacted_Async()
        {
            var (provider, store) = Build();
            using var _ = provider;
            var access = Access(
                requestHeaders: "{\"authorization\":[\"Bearer secret\"],\"Accept\":[\"*/*\"],\"X-API-KEY\":\"k\"}",
                responseHeaders: "{\"Set-Cookie\":[\"session=1\"],\"Content-Type\":[\"application/json\"]}");

            (await SaveAsync(provider, new AuditBatchItem(access))).AllStored.Should().BeTrue();

            var stored = store.AccessLogs.Should().ContainSingle().Subject;
            var request = JObject.Parse(stored.Request!.Headers!);
            request["authorization"]!.ToString().Should().Be("***");
            request["X-API-KEY"]!.ToString().Should().Be("***");
            request["Accept"]![0]!.ToString().Should().Be("*/*", "other headers are kept");
            var response = JObject.Parse(stored.Response!.Headers!);
            response["Set-Cookie"]!.ToString().Should().Be("***");
            response["Content-Type"]![0]!.ToString().Should().Be("application/json");
            stored.Metadata["Redacted"]!.Select(t => t.ToString()).Should()
                .BeEquivalentTo("authorization", "X-API-KEY", "Set-Cookie");
            stored.Request.Headers.Should().NotContain("secret");
        }

        [Fact(DisplayName = "Headers the app cut to the column's length are still masked, though no longer JSON")]
        public async Task Cut_headers_are_redacted_Async()
        {
            var (provider, store) = Build();
            using var _ = provider;
            // What UseAudit() writes, cut by RequestInfo to the column's length: the long header makes it invalid JSON.
            var cut = "{\"Authorization\":[\"Bearer secret\"],\"X-Long\":[\"" + new string('x', 100_000) + "\"]}";
            var access = Access(requestHeaders: cut,
                // Cut inside the secret itself, and inside an array of values.
                responseHeaders: "{\"Content-Type\":\"text/plain\",\"set-cookie\":[\"a=1\",\"session=sec");

            (await SaveAsync(provider, new AuditBatchItem(access))).AllStored.Should().BeTrue();

            var stored = store.AccessLogs.Should().ContainSingle().Subject;
            stored.Request!.Headers.Should().NotBeNull().And.NotContain("secret").And.StartWith("{\"Authorization\":\"***\",");
            stored.Request.Headers!.Length.Should().BeLessThan(cut.Length, "the app cut it");
            stored.Response!.Headers.Should().Be("{\"Content-Type\":\"text/plain\",\"set-cookie\":\"***\"");
            stored.Metadata["Redacted"]!.Select(t => t.ToString()).Should().BeEquivalentTo("Authorization", "set-cookie");
        }

        [Fact(DisplayName = "A cut never splits a character made of two UTF-16 units")]
        public async Task Truncation_keeps_surrogate_pairs_whole_Async()
        {
            var (provider, store) = Build(maxBodyLength: 101);
            using var _ = provider;
            // 100 characters, then an emoji (a surrogate pair) on the 101st and 102nd.
            var body = new string('b', 100) + "\U0001F600" + "bb";
            var changes = "{\"N\":\"" + string.Concat(Enumerable.Repeat("\U0001F600", 40)) + "\"}";
            var access = Access(body: body);

            (await SaveAsync(provider, new AuditBatchItem(access, new[] { Data(changes) }))).AllStored.Should().BeTrue();

            store.AccessLogs.Should().ContainSingle().Which.Request!.Data.Should().Be(new string('b', 100));
            JObject.Parse(store.DataAudits.Should().ContainSingle().Subject.Changes)["$partial"]!.ToString()
                .Should().NotBeEmpty();
            // Changes are cut to whatever fits: over a range of limits, some cuts land inside an emoji.
            for (var limit = 60; limit <= 140; limit++)
            {
                var partial = (string)JObject.Parse(AuditTruncation.TruncateJson(changes, limit))["$partial"]!;
                (partial.Length > 0 && char.IsHighSurrogate(partial[^1])).Should().BeFalse(
                    $"half an emoji is not valid UTF-16 (limit {limit})");
            }
        }

        [Fact(DisplayName = "Long bodies, errors and changes are cut, with the original sizes noted, and changes stay valid JSON")]
        public async Task Long_values_are_truncated_Async()
        {
            var (provider, store) = Build(maxBodyLength: 16);
            using var _ = provider;
            var body = new string('b', 40);
            var changes = "{\"Notes\":\"" + new string('c', 100) + "\"}";
            var access = Access(body: body, responseBody: "short", error: new string('e', 20));

            (await SaveAsync(provider, new AuditBatchItem(access, new[] { Data(changes) }))).AllStored.Should().BeTrue();

            var stored = store.AccessLogs.Should().ContainSingle().Subject;
            stored.Request!.Data.Should().Be(new string('b', 16));
            stored.Response!.Data.Should().Be("short", "a value under the limit is kept");
            stored.Response.Err.Should().HaveLength(16);
            var sizes = (JObject)stored.Metadata["Truncated"]!;
            sizes.Properties().Select(p => (p.Name, (int)p.Value)).Should().BeEquivalentTo(new[] { ("Req_Data", 40), ("Res_Err", 20) });

            var change = JObject.Parse(store.DataAudits.Should().ContainSingle().Subject.Changes);
            ((bool)change["$truncated"]!).Should().BeTrue();
            ((int)change["$originalSize"]!).Should().Be(changes.Length);
        }

        [Fact(DisplayName = "Processors run in Order, around the built-ins")]
        public async Task Processors_run_in_order_Async()
        {
            var (provider, store) = Build(c => c.AddProcessor<Late>().AddProcessor<Between>().AddProcessor<Early>());
            using var _ = provider;
            var access = Access(requestHeaders: "{\"Authorization\":\"x\"}", body: new string('b', 40));

            await SaveAsync(provider, new AuditBatchItem(access));

            var log = provider.GetRequiredService<ProcessorLog>();
            log.Calls.Should().Equal(
                "Early: headers not redacted",
                "Between: headers redacted, body 40",
                "Late: body 16");
            store.AccessLogs.Should().ContainSingle();
        }

        [Fact(DisplayName = "A processor that throws or rejects fails only its record; the others are stored")]
        public async Task A_failing_processor_affects_only_its_record_Async()
        {
            var (provider, store) = Build(c => c.AddProcessor<Picky>());
            using var _ = provider;
            var good = Access("good");
            var refused = Access("refuse");
            var broken = Access("throw");
            var change = Data();

            var result = await SaveAsync(provider, new AuditBatchItem(good, new[] { change }), new AuditBatchItem(refused),
                new AuditBatchItem(broken));

            result.Results.Single(r => r.Id == good.Id).Outcome.Should().Be(AuditRecordOutcome.Stored);
            result.Results.Single(r => r.Id == change.Id).Outcome.Should().Be(AuditRecordOutcome.Stored);
            var no = result.Results.Single(r => r.Id == refused.Id);
            no.Outcome.Should().Be(AuditRecordOutcome.Rejected);
            no.Reason.Should().Be("not wanted");
            var failed = result.Results.Single(r => r.Id == broken.Id);
            failed.Outcome.Should().Be(AuditRecordOutcome.Rejected, "retrying a processor's failure won't help");
            failed.Reason.Should().Contain(nameof(Picky)).And.Contain("processor bug");
            store.AccessLogs.Select(a => a.Action).Should().Equal("good");
            store.DataAudits.Should().ContainSingle();
        }

        [Fact(DisplayName = "A duplicate isn't processed again")]
        public async Task Duplicates_are_not_processed_Async()
        {
            var (provider, _) = Build(c => c.AddProcessor<Early>());
            using var __ = provider;
            var access = Access(requestHeaders: "{}");

            var first = await SaveAsync(provider, new AuditBatchItem(access));
            var replay = await SaveAsync(provider, new AuditBatchItem(access));

            first.Results.Should().ContainSingle().Which.Outcome.Should().Be(AuditRecordOutcome.Stored);
            replay.Results.Should().ContainSingle().Which.Outcome.Should().Be(AuditRecordOutcome.Duplicate);
            provider.GetRequiredService<ProcessorLog>().Calls.Should().ContainSingle();
        }

        public sealed class ProcessorLog
        {
            public List<string> Calls { get; } = new();
        }

        private abstract class Recording(ProcessorLog log, int order) : IAuditRecordProcessor
        {
            public int Order => order;

            public ValueTask<ProcessorResult> ProcessAsync(AuditRecordEnvelope record, CancellationToken cancellationToken)
            {
                if (record.Access is { } access)
                {
                    log.Calls.Add(GetType().Name + ": " + Describe(access));
                }
                return new(ProcessorResult.Continue);
            }

            protected abstract string Describe(AccessLog access);
        }

        private sealed class Early(ProcessorLog log) : Recording(log, 10)
        {
            protected override string Describe(AccessLog access)
                => access.Request!.Headers!.Contains("***") ? "headers redacted" : "headers not redacted";
        }

        private sealed class Between(ProcessorLog log) : Recording(log, 150)
        {
            protected override string Describe(AccessLog access)
                => (access.Request!.Headers!.Contains("***") ? "headers redacted" : "headers not redacted")
                    + ", body " + access.Request.Data!.Length;
        }

        private sealed class Late(ProcessorLog log) : Recording(log, 300)
        {
            protected override string Describe(AccessLog access) => "body " + access.Request!.Data!.Length;
        }

        private sealed class Picky : IAuditRecordProcessor
        {
            public int Order => 50;

            public ValueTask<ProcessorResult> ProcessAsync(AuditRecordEnvelope record, CancellationToken cancellationToken)
                => record.Access?.Action switch
                {
                    "refuse" => new(ProcessorResult.Reject("not wanted")),
                    "throw" => throw new InvalidOperationException("processor bug"),
                    _ => new(ProcessorResult.Continue)
                };
        }
    }
}
