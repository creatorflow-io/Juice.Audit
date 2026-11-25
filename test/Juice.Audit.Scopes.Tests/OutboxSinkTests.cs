using FluentAssertions;
using Juice.Audit.Messaging;
using Juice.Audit.Messaging.Collector;
using Juice.Audit.Messaging.Outbox;
using Juice.Domain.Events;
using Juice.MediatR;
using Juice.Messaging.Integrations;
using Juice.Messaging.Policies;
using Juice.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using static Juice.Audit.Scopes.Tests.OutboxFixture;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// Data changes made under <c>TransactionBehavior</c> go into the transaction's outbox (plan.md T-OUTBOX-1..6): they
    /// commit or roll back with the business change, and every change the outbox can't take is still audited, best
    /// effort, and counted.
    /// </summary>
    [InitializeMessageContext]
    public sealed class OutboxSinkTests : IDisposable
    {
        private const string Entries = "juice.audit.outbox.entries";
        private readonly OutboxFixture _db = new();

        public void Dispose() => _db.Dispose();

        [Fact(DisplayName = "T-OUTBOX-1: a commit puts every change in one outbox event, and the access record counts them")]
        public async Task A_commit_puts_every_change_in_one_event_Async()
        {
            using var app = _db.BuildApp();
            using var meter = ListenTo(app);

            await RunInScopeAsync(app, m => m.Send(new PlaceOrders(3)).AsTask());

            var evt = _db.OutboxEvents().Should().ContainSingle().Subject;
            evt.Entries.Should().HaveCount(3);
            evt.Entries.Select(e => e.Id).Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
            evt.App.Should().Be("orders-api");
            evt.TransactionId.Should().NotBeEmpty();
            evt.Part.Should().Be(1);

            var (accessLog, dataAudits) = Records(app).Should().ContainSingle().Subject;
            dataAudits.Should().BeEmpty("the changes travel with the outbox, not with the access record");
            accessLog.Metadata["OutboxedChanges"]!.Value<int>().Should().Be(3);
            accessLog.Metadata.ContainsKey("UncommittedChanges").Should().BeFalse();
            accessLog.Metadata.ContainsKey("BestEffortChanges").Should().BeFalse();
            evt.Entries.Should().OnlyContain(e => e.TraceId == accessLog.TraceId);
            meter.Sum(Entries, ("outcome", "outboxed")).Should().Be(3);
        }

        [Fact(DisplayName = "T-OUTBOX-2: a rollback leaves no outbox event, and the access record counts the changes as uncommitted")]
        public async Task A_rollback_leaves_no_event_Async()
        {
            using var app = _db.BuildApp();
            app.GetRequiredService<SampleSwitches>().FailCommit = true;

            var run = () => RunInScopeAsync(app, m => m.Send(new PlaceOrders(3)).AsTask());

            await run.Should().ThrowAsync<InvalidOperationException>();
            _db.OutboxEvents().Should().BeEmpty();
            var (accessLog, dataAudits) = Records(app).Should().ContainSingle().Subject;
            dataAudits.Should().BeEmpty();
            accessLog.Metadata["UncommittedChanges"]!.Value<int>().Should().Be(3);
            accessLog.Metadata.ContainsKey("OutboxedChanges").Should().BeFalse();
        }

        [Fact(DisplayName = "T-OUTBOX-3: changes of a later dispatch round join the event already in the outbox")]
        public async Task Changes_of_a_later_round_join_the_event_Async()
        {
            using var app = _db.BuildApp();

            await RunInScopeAsync(app, m => m.Send(new PlaceOrders(1, WithLine: true)).AsTask());

            var evt = _db.OutboxEvents().Should().ContainSingle().Subject;
            evt.Entries.Should().HaveCount(2, "the line was added after the event, and the outbox saved it later");
            evt.Entries.Select(e => e.Tbl).Distinct().Should().HaveCount(2);
            Records(app).Single().AccessLog.Metadata["OutboxedChanges"]!.Value<int>().Should().Be(2);
        }

        [Fact(DisplayName = "T-OUTBOX-4: a change saved without a transaction goes with its access record")]
        public async Task No_transaction_is_best_effort_Async()
        {
            using var app = _db.BuildApp();
            using var meter = ListenTo(app);

            // A plain save, outside TransactionBehavior: no transaction id.
            await RunPlainSaveInScopeAsync(app);

            _db.OutboxEvents().Should().BeEmpty();
            var (accessLog, dataAudits) = Records(app).Should().ContainSingle().Subject;
            dataAudits.Should().ContainSingle();
            accessLog.Metadata["BestEffortChanges"]!.Value<int>().Should().Be(1);
            meter.Sum(Entries, ("outcome", "best_effort"), ("reason", "no_transaction")).Should().Be(1);
        }

        [Fact(DisplayName = "T-OUTBOX-4: a transaction no AddAuditOutbox context owns goes with its access record")]
        public async Task No_binding_is_best_effort_Async()
        {
            using var app = _db.BuildApp();
            using var meter = ListenTo(app);
            var transaction = Guid.NewGuid();

            await RunInScopeAsync(app, async mediator =>
            {
                var change = new AuditEvent<Order>("Modified");
                change.SetAuditRecord(new AuditRecord("Orders") { KeyValues = { ["Id"] = 1 } });
                change.SetTransactionId(transaction);
                await mediator.Publish(change);
                await mediator.Publish(new TransactionCommitted(transaction));
            });

            var (accessLog, dataAudits) = Records(app).Should().ContainSingle().Subject;
            dataAudits.Should().ContainSingle();
            accessLog.Metadata["BestEffortChanges"]!.Value<int>().Should().Be(1);
            meter.Sum(Entries, ("outcome", "best_effort"), ("reason", "no_binding")).Should().Be(1);
        }

        [Theory(DisplayName = "T-OUTBOX-4: a policy that routes juice.audit nowhere sends the changes best effort")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task No_route_is_best_effort_Async(bool localChannelOnly)
        {
            using var app = _db.BuildApp(localChannelOnly
                ? new[] { new PublishRoute("local-channel", "juice.audit") }
                : Array.Empty<PublishRoute>());
            using var meter = ListenTo(app);

            await RunInScopeAsync(app, m => m.Send(new PlaceOrders(2)).AsTask());

            _db.OutboxEvents().Should().BeEmpty();
            var (accessLog, dataAudits) = Records(app).Should().ContainSingle().Subject;
            dataAudits.Should().HaveCount(2, "the transaction committed, so its best-effort changes are carried");
            accessLog.Metadata["BestEffortChanges"]!.Value<int>().Should().Be(2);
            meter.Sum(Entries, ("outcome", "best_effort"), ("reason", "no_route")).Should().Be(2);
        }

        [Fact(DisplayName = "T-OUTBOX-4: a change of another tenant than the outbox stamps goes best effort")]
        public async Task Tenant_mismatch_is_best_effort_Async()
        {
            using var app = _db.BuildApp(tenant: "tenant-a");
            using var meter = ListenTo(app);

            await RunInScopeAsync(app, m => m.Send(new PlaceOrders(1)).AsTask(), tenant: "tenant-b");

            _db.OutboxEvents().Should().BeEmpty();
            Records(app).Single().DataAudits.Should().ContainSingle();
            meter.Sum(Entries, ("outcome", "best_effort"), ("reason", "tenant_mismatch")).Should().Be(1);
        }

        [Fact(DisplayName = "T-OUTBOX-4: a failure in the sink sends the change best effort and doesn't fail the command")]
        public async Task A_sink_failure_is_best_effort_Async()
        {
            using var app = _db.BuildApp(throwingPolicy: true);
            using var meter = ListenTo(app);

            await RunInScopeAsync(app, m => m.Send(new PlaceOrders(1)).AsTask());

            Records(app).Single().DataAudits.Should().ContainSingle();
            meter.Sum(Entries, ("outcome", "best_effort"), ("reason", "error")).Should().Be(1);
            using var context = _db.CreateContext();
            context.Orders.Count().Should().Be(1, "audit never breaks the host");
        }

        [Fact(DisplayName = "T-OUTBOX-5: over MaxEntriesPerEvent the changes are split into parts")]
        public async Task Changes_over_the_limit_are_split_Async()
        {
            using var app = _db.BuildApp(configure: o => o.MaxEntriesPerEvent = 2);

            await RunInScopeAsync(app, m => m.Send(new PlaceOrders(5)).AsTask());

            var events = _db.OutboxEvents().OrderBy(e => e.Part).ToList();
            events.Select(e => e.Part).Should().Equal(1, 2, 3);
            events.Select(e => e.Entries.Count).Should().Equal(2, 2, 1);
            events.Select(e => e.TransactionId).Distinct().Should().ContainSingle();
        }

        [Fact(DisplayName = "T-OUTBOX-5: a value over the limit is truncated to valid JSON, marked, and keeps nearly all it can")]
        public void A_change_over_the_byte_limit_is_truncated()
        {
            var changes = new JObject { ["CurrentValues"] = new JObject { ["Body"] = new string('x', 10_000) } }
                .ToString(Newtonsoft.Json.Formatting.None);

            var truncated = AuditTruncation.TruncateJson(changes, 1_000);

            var bytes = System.Text.Encoding.UTF8.GetByteCount(truncated);
            bytes.Should().BeLessThanOrEqualTo(1_000).And.BeGreaterThan(990, "the cut is searched for, not halved");
            var json = JObject.Parse(truncated);
            json["$truncated"]!.Value<bool>().Should().BeTrue();
            json["$originalSize"]!.Value<int>().Should().Be(changes.Length);
            json["$partial"]!.Value<string>().Should().NotBeEmpty();
            AuditTruncation.TruncateJson("{}", 1_000).Should().Be("{}");
        }

        /// <summary>Order numbers that are quotes and backslashes, as a column holding JSON or paths: escaped again in the event.</summary>
        private static string Escaped(int length) => string.Concat(Enumerable.Repeat("{\\\"k\\\":\\\"C:\\\\x\\\"}", length / 16));

        [Fact(DisplayName = "T-OUTBOX-5: every event stays under MaxEventBytes as stored, escaping and type names included")]
        public async Task Events_stay_under_the_byte_limit_as_stored_Async()
        {
            using var app = _db.BuildApp(configure: o => o.MaxEventBytes = 8 * 1024);

            await RunInScopeAsync(app, m => m.Send(new PlaceOrders(60, Number: Escaped(300))).AsTask());

            var sizes = _db.OutboxPayloadSizes();
            sizes.Should().HaveCountGreaterThan(1).And.OnlyContain(s => s <= 8 * 1024);
            sizes.Take(sizes.Count - 1).Should().OnlyContain(s => s > 6 * 1024, "a full part is close to the limit");
            _db.OutboxEvents().Sum(e => e.Entries.Count).Should().Be(60, "nothing is cut or lost by the split");
            _db.OutboxEvents().SelectMany(e => e.Entries).Should().NotContain(e => e.Changes.Contains("$truncated"));
        }

        [Fact(DisplayName = "T-OUTBOX-5: a change too big for an event of its own is cut to fit it, as stored")]
        public async Task A_huge_change_is_cut_to_fit_as_stored_Async()
        {
            using var app = _db.BuildApp(configure: o => o.MaxEventBytes = 8 * 1024);

            await RunInScopeAsync(app, m => m.Send(new PlaceOrders(1, Number: Escaped(40_000))).AsTask());

            var size = _db.OutboxPayloadSizes().Should().ContainSingle().Subject;
            size.Should().BeLessThanOrEqualTo(8 * 1024).And.BeGreaterThan(7 * 1024, "it keeps nearly all it can");
            var changes = JObject.Parse(_db.OutboxEvents().Single().Entries.Single().Changes);
            changes["$truncated"]!.Value<bool>().Should().BeTrue();
        }

        [Fact(DisplayName = "T-OUTBOX-4: a transaction the app opens itself goes with its access record: nothing saves its outbox")]
        public async Task An_unmanaged_transaction_is_best_effort_Async()
        {
            using var app = _db.BuildApp();
            using var meter = ListenTo(app);

            await RunWithServicesInScopeAsync(app, async services =>
            {
                var context = services.GetRequiredService<SampleOrderContext>();
                await using var transaction = await context.Database.BeginTransactionAsync();
                context.Orders.Add(new Order("MANUAL-1"));
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
            });

            Records(app).Single().DataAudits.Should().ContainSingle("the change isn't lost")
                .Which.Changes.Should().Contain("MANUAL-1");
            // Core gives a change a transaction only under TransactionBehavior; the sink checks it too ("unmanaged").
            meter.Sum(Entries, ("outcome", "best_effort")).Should().Be(1);
            meter.Sum(Entries, ("outcome", "outboxed")).Should().Be(0);
            _db.OutboxEvents().Should().BeEmpty();
        }

        [Fact(DisplayName = "T-OUTBOX-2: changes of a transaction that rolled back aren't stored by the scope's next transaction")]
        public async Task A_rolled_back_event_is_not_saved_later_Async()
        {
            using var app = _db.BuildApp();
            var switches = app.GetRequiredService<SampleSwitches>();

            await RunWithServicesInScopeAsync(app, async services =>
            {
                var mediator = services.GetRequiredService<IMediator>();
                // The order's change reaches the outbox; saving the line then fails, before the outbox is saved.
                switches.FailOnLine = true;
                var failing = () => mediator.Send(new PlaceOrders(1, WithLine: true, Number: "ROLLED-")).AsTask();
                await failing.Should().ThrowAsync<InvalidOperationException>();
                switches.FailOnLine = false;
                // The same scope goes on, as a job loop or a request that handles the error would. Since Juice 10.15
                // (creatorflow-io/Juice#126) the failed command leaves nothing on the context or the outbox.
                await mediator.Send(new PlaceOrders(1, Number: "KEPT-"));
            });

            var events = _db.OutboxEvents();
            events.Should().ContainSingle("the rolled back event was discarded with its command");
            events.SelectMany(e => e.Entries).Select(e => e.Changes).Should().ContainSingle()
                .Which.Should().Contain("KEPT-0", "only the next transaction's change is stored");
        }

        [Fact(DisplayName = "T-OUTBOX-2: an event still pending when another transaction saves the outbox is written without its changes")]
        public void A_pending_event_of_another_transaction_has_no_changes()
        {
            // The guard for a Juice before 10.15, whose failed command left its outbox events pending.
            var evt = new DataChangesAudited { Entries = { new DataChangeEntry { Tbl = "Orders" } } };

            evt.IsInTransaction = () => true;
            evt.ShouldSerializeEntries().Should().BeTrue("its own transaction saves it");
            evt.IsInTransaction = () => false;
            evt.ShouldSerializeEntries().Should().BeFalse("another transaction saves it: its changes were rolled back");
            evt.IsInTransaction = () => throw new ObjectDisposedException("context");
            evt.ShouldSerializeEntries().Should().BeTrue("its scope is gone: kept, as without the guard");
            evt.IsInTransaction = null;
            evt.ShouldSerializeEntries().Should().BeTrue();
        }

        [Fact(DisplayName = "T-OUTBOX-6: the same event consumed twice stores each change once, and isn't audited itself")]
        public async Task The_same_event_consumed_twice_stores_each_change_once_Async()
        {
            using (var app = _db.BuildApp())
            {
                await RunInScopeAsync(app, m => m.Send(new PlaceOrders(2)).AsTask());
            }
            var evt = _db.OutboxEvents().Single();
            var store = new MemoryAuditStore();
            using var collector = BuildCollector(store);

            await ConsumeAsync(collector, evt);
            await ConsumeAsync(collector, evt);

            store.DataAudits.Select(d => d.Id).Should().BeEquivalentTo(evt.Entries.Select(e => e.Id));
            store.DataAudits.Should().OnlyContain(d => d.TraceId == evt.Entries[0].TraceId);
            Records(collector).Should().BeEmpty("the collector's own consumption is not audited (R11)");
        }

        [Fact(DisplayName = "R5: a change made with no audit scope open still goes through the outbox")]
        public async Task A_change_without_a_scope_goes_through_the_outbox_Async()
        {
            using var app = _db.BuildApp(mediatorAudit: false);
            using (var scope = app.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new PlaceOrders(2));
            }

            _db.OutboxEvents().Should().ContainSingle().Which.Entries.Should().HaveCount(2);
            Records(app).Should().BeEmpty("there is no scope, so no access record");
        }

        [Fact(DisplayName = "R5: RequireScope keeps changes made with no audit scope out, as before 10.7")]
        public async Task RequireScope_keeps_changes_without_a_scope_out_Async()
        {
            using var app = _db.BuildApp(configure: o => o.RequireScope = true, mediatorAudit: false);
            using (var scope = app.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new PlaceOrders(2));
            }

            _db.OutboxEvents().Should().BeEmpty();
        }

        [Fact(DisplayName = "Without AddAuditOutbox the changes go with the access record, as before 10.7")]
        public async Task Without_the_outbox_nothing_changes_Async()
        {
            using var app = _db.BuildApp(sink: false);

            await RunInScopeAsync(app, m => m.Send(new PlaceOrders(2)).AsTask());

            _db.OutboxEvents().Should().BeEmpty();
            var (accessLog, dataAudits) = Records(app).Should().ContainSingle().Subject;
            dataAudits.Should().HaveCount(2);
            accessLog.Metadata.ContainsKey("BestEffortChanges").Should().BeFalse();
            accessLog.Metadata.ContainsKey("OutboxedChanges").Should().BeFalse();
        }

        private async Task RunPlainSaveInScopeAsync(IServiceProvider app)
        {
            using var scope = app.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<Api.Scopes.AuditScopeRunner>();
            await runner.RunAsync(
                new Api.Scopes.AuditScopeRequest("save", Api.Scopes.AuditScopeMethods.Request, "save", null),
                async () =>
                {
                    var context = scope.ServiceProvider.GetRequiredService<SampleOrderContext>();
                    context.Orders.Add(new Order("plain"));
                    await context.SaveChangesAsync();
                    return true;
                },
                _ => Api.Scopes.AuditScopeOutcome.Ok);
        }

        internal static async Task ConsumeAsync(IServiceProvider collector, DataChangesAudited evt)
        {
            using var scope = collector.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IntegrationEventDispatcher>()
                .DispatchAsync(evt, new EventDispatchContext([typeof(DataChangesAuditedHandler)],
                    nameof(DataChangesAudited), evt.TenantId, "orders-api"));
            result.Should().Be(EventDispatchResult.Success);
        }
    }
}
