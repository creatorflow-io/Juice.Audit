using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.Api.Scopes;
using Juice.Audit.AspNetCore.Middleware;
using Juice.Messaging;
using Juice.Messaging.Integrations;
using Microsoft.Extensions.DependencyInjection;
using static Juice.Audit.Scopes.Tests.AuditScopeFixture;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// <c>AddMessageAudit()</c>: an integration event's handlers run in an audit scope, as a request does under
    /// <c>UseAudit()</c>, saved under the message's trace id.
    /// </summary>
    public class MessageAuditTests
    {
        [Fact]
        public async Task Data_changed_by_a_handler_is_saved_under_the_message_trace_Async()
        {
            using var provider = Build(messageAudit: true);
            using var consumer = new Activity("process OrderPlaced").SetIdFormat(ActivityIdFormat.W3C).Start();
            var evt = new OrderPlaced();

            var result = await DispatchAsync(provider, evt, typeof(ChangingHandler), tenantId: "tenant-a");

            result.Should().Be(EventDispatchResult.Success);
            var saved = provider.Records().Should().ContainSingle().Subject;
            var traceId = consumer.TraceId.ToHexString();
            saved.AccessLog.TraceId.Should().Be(traceId);
            saved.AccessLog.Action.Should().Be(nameof(OrderPlaced));
            saved.AccessLog.TenantId.Should().Be("tenant-a");
            saved.AccessLog.User.Should().BeNull();
            saved.AccessLog.Request!.Method.Should().Be(AuditScopeMethods.Message);
            saved.AccessLog.Request.Path.Should().Be(nameof(OrderPlaced));
            saved.AccessLog.Response!.Status.Should().Be(200);
            saved.AccessLog.Metadata["MessageId"]!.ToString().Should().Be(evt.MessageId.ToString());
            saved.AccessLog.Metadata["SpanId"]!.ToString().Should().Be(consumer.SpanId.ToHexString());
            saved.AccessLog.Metadata["Source"]!.ToString().Should().Be("orders-api");
            ((bool)saved.AccessLog.Metadata["DataAuditRequest"]!).Should().BeTrue();
            var change = saved.DataAudits.Should().ContainSingle().Subject;
            change.TraceId.Should().Be(traceId);
            change.Tbl.Should().Be("Orders");
            change.TenantId.Should().Be("tenant-a");
        }

        [Fact]
        public async Task Command_sent_by_a_handler_joins_the_message_scope_Async()
        {
            using var provider = Build(messageAudit: true, mediatorAudit: true);

            await DispatchAsync(provider, new OrderPlaced(), typeof(SendingHandler));

            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.AccessLog.Request!.Method.Should().Be(AuditScopeMethods.Message);
            saved.DataAudits.Should().ContainSingle();
        }

        [Fact]
        public async Task Handler_that_changes_nothing_saves_nothing_Async()
        {
            using var provider = Build(messageAudit: true);

            var result = await DispatchAsync(provider, new OrderPlaced(), typeof(ReadingHandler));

            result.Should().Be(EventDispatchResult.Success);
            provider.Records().Should().BeEmpty();
        }

        [Fact]
        public async Task Change_before_a_handler_failed_is_saved_with_the_error_Async()
        {
            using var provider = Build(messageAudit: true);

            var result = await DispatchAsync(provider, new OrderPlaced(), typeof(ChangingThenFailingHandler));

            result.Should().Be(EventDispatchResult.Failure);
            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.AccessLog.Response!.Status.Should().Be(500);
            saved.AccessLog.Response.Msg.Should().Contain("payment refused");
            saved.AccessLog.Response.Err.Should().Contain(nameof(ChangingThenFailingHandler));
            saved.DataAudits.Should().ContainSingle();
        }

        [Fact]
        public async Task Each_message_gets_its_own_audit_scope_Async()
        {
            using var provider = Build(messageAudit: true);

            await DispatchAsync(provider, new OrderPlaced(), typeof(ChangingHandler));
            await DispatchAsync(provider, new OrderPlaced(), typeof(ChangingHandler));

            var records = provider.Records();
            records.Should().HaveCount(2);
            records.Should().OnlyContain(r => r.DataAudits.Length == 1);
        }

        [Fact]
        public async Task Filter_rule_saves_a_message_that_changed_nothing_Async()
        {
            using var provider = Build(messageAudit: true,
                configure: o => o.Filter = new AuditFilterOptions().Include(nameof(OrderPlaced), AuditScopeMethods.Message));

            await DispatchAsync(provider, new OrderPlaced(), typeof(ReadingHandler));

            var saved = provider.Records().Should().ContainSingle().Subject;
            ((bool)saved.AccessLog.Metadata["FilterMatched"]!).Should().BeTrue();
            saved.DataAudits.Should().BeEmpty();
        }

        [Fact]
        public async Task Without_message_audit_a_change_in_a_handler_is_skipped_Async()
        {
            using var provider = Build();

            var result = await DispatchAsync(provider, new OrderPlaced(), typeof(ChangingHandler));

            // The data event handler finds no audit scope and skips, instead of failing the handler.
            result.Should().Be(EventDispatchResult.Success);
            provider.Records().Should().BeEmpty();
        }

        private static Task<EventDispatchResult> DispatchAsync(IServiceProvider provider, OrderPlaced evt, Type handler,
            string? tenantId = null)
            => provider.GetRequiredService<IntegrationEventDispatcher>()
                .DispatchAsync(evt, new EventDispatchContext([handler], nameof(OrderPlaced), tenantId, "orders-api"));
    }
}
