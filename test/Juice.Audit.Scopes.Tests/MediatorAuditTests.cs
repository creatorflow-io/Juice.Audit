using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.Api.Scopes;
using Juice.MediatR;
using Microsoft.Extensions.DependencyInjection;
using static Juice.Audit.Scopes.Tests.AuditScopeFixture;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// <c>AddMediatorAudit()</c>: mediator requests and notifications outside an audited request or message (a
    /// background job) run in their own audit scope; inside one they join it.
    /// </summary>
    public class MediatorAuditTests
    {
        [Fact]
        public async Task Command_outside_any_scope_is_saved_under_the_current_trace_Async()
        {
            using var provider = Build(mediatorAudit: true);
            using var job = new Activity("job").SetIdFormat(ActivityIdFormat.W3C).Start();
            using var scope = provider.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ChangeOrder());

            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.AccessLog.TraceId.Should().Be(job.TraceId.ToHexString());
            saved.AccessLog.Request!.Method.Should().Be(AuditScopeMethods.Request);
            saved.AccessLog.Request.Path.Should().Be(nameof(ChangeOrder));
            saved.AccessLog.Response!.Status.Should().Be(200);
            saved.DataAudits.Should().ContainSingle().Which.TraceId.Should().Be(job.TraceId.ToHexString());
        }

        [Fact]
        public async Task Commands_in_one_DI_scope_get_a_record_each_Async()
        {
            using var provider = Build(mediatorAudit: true);
            using var scope = provider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            await mediator.Send(new ChangeOrder());
            await mediator.Send(new ChangeOrder());

            var records = provider.Records();
            records.Should().HaveCount(2);
            records.Should().OnlyContain(r => r.DataAudits.Length == 1);
        }

        [Fact]
        public async Task Command_inside_an_open_scope_joins_it_Async()
        {
            using var provider = Build(mediatorAudit: true);
            using var scope = provider.CreateScope();
            // As AuditMiddleware opens one for an HTTP request.
            var accessor = scope.ServiceProvider.GetRequiredService<IAuditContextAccessor>();
            accessor.Init("orders_pay", "alice");

            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ChangeOrder());

            provider.Records().Should().BeEmpty();
            accessor.AuditContext.AuditEntries.Should().ContainSingle();
        }

        [Fact]
        public async Task Change_without_a_user_takes_the_user_of_its_scope_Async()
        {
            using var provider = Build(mediatorAudit: true);
            using var scope = provider.CreateScope();
            var accessor = scope.ServiceProvider.GetRequiredService<IAuditContextAccessor>();
            accessor.Init("orders_pay", "alice");

            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ChangeOrder());

            accessor.AuditContext.AuditEntries.Should().ContainSingle().Which.User.Should().Be("alice");
        }

        [Fact]
        public async Task Failed_command_is_saved_with_the_error_Async()
        {
            using var provider = Build(mediatorAudit: true);
            using var scope = provider.CreateScope();

            var send = async () => await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ChangeOrder(Fail: true));

            await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("order is locked");
            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.AccessLog.Response!.Status.Should().Be(500);
            saved.AccessLog.Response.Msg.Should().Be("order is locked");
            saved.DataAudits.Should().ContainSingle();
        }

        [Fact]
        public async Task Notification_that_changes_data_is_saved_Async()
        {
            using var provider = Build(mediatorAudit: true);
            using var scope = provider.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(new OrderShipped());

            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.AccessLog.Request!.Method.Should().Be(AuditScopeMethods.Notification);
            saved.AccessLog.Request.Path.Should().Be(nameof(OrderShipped));
            saved.DataAudits.Should().ContainSingle();
        }

        [Fact]
        public async Task Data_change_notification_does_not_open_a_scope_Async()
        {
            using var provider = Build(mediatorAudit: true);
            using var scope = provider.CreateScope();

            // A SaveChanges outside any scope: the change has no record to join.
            await ChangeAsync(scope.ServiceProvider.GetRequiredService<IMediator>());

            provider.Records().Should().BeEmpty();
        }
    }
}
