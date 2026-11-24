using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.Domain;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.EF;
using Juice.Audit.Tests.Host.Sample;
using Juice.XUnit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Tests
{
    /// <summary>
    /// A single app runs the pipeline in-process (US6): <c>AddAuditPipeline</c> with <c>Audit:Mode = InProcess</c>, no
    /// broker and no separate service, gives the guarantees of US1 and US2. Through <c>Juice.Audit.Tests.Host</c> on a
    /// real audit database: <c>ConnectionStrings__SqlServerConnection</c>.
    /// </summary>
    public sealed class InProcessModeTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public InProcessModeTests(WebApplicationFactory<Program> factory) => _factory = factory;

        private static async Task<(HttpResponseMessage Response, string TraceId)> PlaceOrderAsync(
            WebApplicationFactory<Program> factory, bool fail = false)
        {
            var client = factory.CreateClient();
            var response = await client.PostAsync($"/orders?number=SO-{Guid.NewGuid():N}&fail={fail}", null);
            return (response, response.Headers.GetValues("X-Trace-Id").Single());
        }

        private static async Task<(AccessLog? Access, List<DataAudit> Changes)> ReadAsync(
            WebApplicationFactory<Program> factory, string traceId)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            return (await db.AccessLogs.AsNoTracking().FirstOrDefaultAsync(a => a.Request!.TraceId == traceId),
                await db.AuditEntries.AsNoTracking().Where(d => d.TraceId == traceId).ToListAsync());
        }

        private static async Task ShouldSucceedAsync(HttpResponseMessage response)
            => response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());

        private static async Task EventuallyAsync(Func<Task<bool>> condition, int timeoutSeconds = 30)
        {
            var watch = Stopwatch.StartNew();
            while (!await condition())
            {
                watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(timeoutSeconds), "the condition should hold by then");
                await Task.Delay(200);
            }
        }

        [IgnoreOnCIFact(DisplayName = "In-process: a committed change and its access record both reach the audit database")]
        public async Task A_committed_change_is_audited_Async()
        {
            var (response, traceId) = await PlaceOrderAsync(_factory);
            await ShouldSucceedAsync(response);

            await EventuallyAsync(async () => (await ReadAsync(_factory, traceId)).Changes.Count > 0);

            var (access, changes) = await ReadAsync(_factory, traceId);
            access.Should().NotBeNull();
            ((int)access!.Metadata["OutboxedChanges"]!).Should().Be(1, "the change went through the outbox");
            access.Metadata.ContainsKey("BestEffortChanges").Should().BeFalse();
            changes.Should().ContainSingle().Which.Action.Should().Be("Inserted");
        }

        [IgnoreOnCIFact(DisplayName = "In-process: a rolled back change leaves no data audit")]
        public async Task A_rollback_leaves_no_data_audit_Async()
        {
            var (response, traceId) = await PlaceOrderAsync(_factory, fail: true);
            ((int)response.StatusCode).Should().Be(409);

            // A committed order placed after it is delivered: by then the rolled back one would have been too.
            var (after, afterTrace) = await PlaceOrderAsync(_factory);
            await ShouldSucceedAsync(after);
            await EventuallyAsync(async () => (await ReadAsync(_factory, afterTrace)).Changes.Count > 0);

            var (access, changes) = await ReadAsync(_factory, traceId);
            changes.Should().BeEmpty();
            access.Should().NotBeNull("the failed request is still recorded");
            ((int)access!.Metadata["UncommittedChanges"]!).Should().Be(1);
        }

        [IgnoreOnCIFact(DisplayName = "In-process: requests don't wait on the audit database")]
        public async Task Requests_do_not_wait_on_the_database_Async()
        {
            using var slow = _factory.WithWebHostBuilder(host =>
            {
                host.UseSetting("Audit:Export:Synchronous", "false");
                host.ConfigureTestServices(services =>
                {
                    // Every audit write takes 2 s, longer than any request may.
                    var store = services.Last(d => d.ServiceType == typeof(IAuditBatchStore));
                    services.Remove(store);
                    services.AddScoped<IAuditBatchStore>(sp => new SlowStore(
                        (IAuditBatchStore)ActivatorUtilities.CreateInstance(sp, store.ImplementationType!),
                        TimeSpan.FromSeconds(2)));
                });
            });
            await PlaceOrderAsync(slow); // warm up the host

            var watch = Stopwatch.StartNew();
            var (response, traceId) = await PlaceOrderAsync(slow);
            watch.Stop();

            await ShouldSucceedAsync(response);
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1.5), "the access record and the change are written later");
            await EventuallyAsync(async () =>
            {
                var (access, changes) = await ReadAsync(slow, traceId);
                return access != null && changes.Count > 0;
            });
        }

        [Theory(DisplayName = "Audit:Mode is required: missing or unknown, the registration fails and names the key")]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Local")]
        [InlineData("1")]
        public void The_mode_is_required(string? mode)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:Mode"] = mode })
                .Build();

            var register = () => new ServiceCollection()
                .AddMessaging(m => m.AddAuditPipeline<SampleOrderContext>(configuration));

            register.Should().Throw<InvalidOperationException>()
                .Which.Message.Should().Contain("Audit:Mode").And.Contain("Remote").And.Contain("InProcess");
        }

        [Fact(DisplayName = "Remote mode with no collector address fails at registration")]
        public void Remote_mode_needs_an_address()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:Mode"] = "remote" })
                .Build();

            var register = () => new ServiceCollection()
                .AddMessaging(m => m.AddAuditPipeline<SampleOrderContext>(configuration));

            register.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Audit:Grpc:Address");

            var withAddress = () => new ServiceCollection()
                .AddMessaging(m => m.AddAuditPipeline<SampleOrderContext>(configuration,
                    o => o.ConfigureGrpcClient = g => g.Address = new Uri("https://audit:7285")));
            withAddress.Should().NotThrow();
        }

        private sealed class SlowStore : IAuditBatchStore
        {
            private readonly IAuditBatchStore _inner;
            private readonly TimeSpan _delay;

            public SlowStore(IAuditBatchStore inner, TimeSpan delay) => (_inner, _delay) = (inner, delay);

            public Task<IReadOnlyCollection<Guid>> GetExistingAccessLogIdsAsync(IReadOnlyCollection<Guid> ids,
                CancellationToken cancellationToken) => _inner.GetExistingAccessLogIdsAsync(ids, cancellationToken);

            public Task<IReadOnlyCollection<Guid>> GetExistingDataAuditIdsAsync(IReadOnlyCollection<Guid> ids,
                CancellationToken cancellationToken) => _inner.GetExistingDataAuditIdsAsync(ids, cancellationToken);

            public void Add(AccessLog accessLog) => _inner.Add(accessLog);

            public void Add(DataAudit dataAudit) => _inner.Add(dataAudit);

            public async Task SaveChangesAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(_delay, cancellationToken);
                await _inner.SaveChangesAsync(cancellationToken);
            }

            public void ClearTracked() => _inner.ClearTracked();
        }
    }
}
