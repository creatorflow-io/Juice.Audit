using FluentAssertions;
using Juice.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using static Juice.Audit.Scopes.Tests.OutboxFixture;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// SC-001: a committed change reaches the audit store even when the app dies right after the commit, and a rolled
    /// back one never does. Each run commits or rolls back, then throws the app away (its access record and anything
    /// else in memory is lost), and a collector stores what the outbox table holds. The oracle is the business data:
    /// every order in the database has exactly one stored change, and no other change is stored.
    /// <para>Delivery is simulated by reading the outbox table, as the delivery processor does; the processor itself
    /// is Juice core's and tested there.</para>
    /// </summary>
    [InitializeMessageContext]
    public sealed class OutboxCrashTests : IDisposable
    {
        private const int Runs = 100;
        private readonly OutboxFixture _db = new();

        public void Dispose() => _db.Dispose();

        [Fact(DisplayName = "SC-001: committed changes survive the app; rolled back ones never reach the store")]
        public async Task Committed_changes_survive_the_app_Async()
        {
            var store = new MemoryAuditStore();
            using var collector = BuildCollector(store);
            var consumed = new HashSet<Guid>();

            for (var run = 0; run < Runs; run++)
            {
                var rollback = run % 2 == 1;
                using (var app = _db.BuildApp())
                {
                    app.GetRequiredService<SampleSwitches>().FailCommit = rollback;
                    var send = () => RunInScopeAsync(app, m => m.Send(new PlaceOrders(2)).AsTask());
                    if (rollback)
                    {
                        await send.Should().ThrowAsync<InvalidOperationException>();
                    }
                    else
                    {
                        await send();
                    }
                } // the app is gone: whatever it held in memory is lost

                foreach (var evt in _db.OutboxEvents().Where(e => consumed.Add(e.MessageId)))
                {
                    await OutboxSinkTests.ConsumeAsync(collector, evt);
                }
            }

            using var context = _db.CreateContext();
            var orders = context.Orders.Select(o => o.Id).ToList();
            orders.Should().HaveCount(Runs, "half the runs committed two orders each");
            var audited = store.DataAudits.Select(d => JObject.Parse(d.Kvps)["Id"]!.Value<string>()).ToList();
            audited.Should().OnlyHaveUniqueItems();
            audited.Select(Guid.Parse).Should().BeEquivalentTo(orders);
        }
    }
}
