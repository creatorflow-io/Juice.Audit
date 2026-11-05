using FluentAssertions;
using Juice.Audit.Api.Scopes;
using Juice.Domain.Events;
using Juice.MediatR;
using Microsoft.Extensions.DependencyInjection;
using static Juice.Audit.Scopes.Tests.AuditScopeFixture;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// A change made inside a managed transaction (Juice's <c>TransactionBehavior</c> stamps its id on the event) is
    /// saved only when <see cref="TransactionCommitted"/> follows. Otherwise the attempt is saved without it.
    /// </summary>
    public class TransactionAuditTests
    {
        private static async Task RunAsync(IServiceProvider provider, Func<IMediator, Task> work, bool fail = false)
        {
            using var scope = provider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await scope.ServiceProvider.GetRequiredService<AuditScopeRunner>().RunAsync(
                new AuditScopeRequest("work", AuditScopeMethods.Request, "work", null),
                async () =>
                {
                    await work(mediator);
                    return true;
                },
                _ => fail ? new AuditScopeOutcome(500, "failed") : AuditScopeOutcome.Ok);
        }

        private static ValueTask CommitAsync(IMediator mediator, Guid transactionId)
            => mediator.Publish(new TransactionCommitted(transactionId));

        [Fact]
        public async Task Change_in_a_committed_transaction_is_saved_Async()
        {
            using var provider = Build(mediatorAudit: true);
            var transaction = Guid.NewGuid();

            await RunAsync(provider, async mediator =>
            {
                await ChangeAsync(mediator, transactionId: transaction);
                await CommitAsync(mediator, transaction);
            });

            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.DataAudits.Should().ContainSingle();
            saved.AccessLog.Metadata.ContainsKey("UncommittedChanges").Should().BeFalse();
        }

        [Fact]
        public async Task Change_in_a_transaction_that_never_commits_is_dropped_but_the_attempt_is_saved_Async()
        {
            using var provider = Build(mediatorAudit: true);

            await RunAsync(provider, async mediator =>
            {
                await ChangeAsync(mediator, transactionId: Guid.NewGuid());
                await ChangeAsync(mediator, transactionId: Guid.NewGuid());
            }, fail: true);

            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.AccessLog.Response!.Status.Should().Be(500);
            saved.DataAudits.Should().BeEmpty();
            saved.AccessLog.Metadata["UncommittedChanges"]!.ToString().Should().Be("2");
        }

        [Fact]
        public async Task Failed_work_keeps_what_an_earlier_transaction_committed_Async()
        {
            using var provider = Build(mediatorAudit: true);
            var first = Guid.NewGuid();

            await RunAsync(provider, async mediator =>
            {
                await ChangeAsync(mediator, "Paid", first);
                await CommitAsync(mediator, first);
                await ChangeAsync(mediator, "Shipped", Guid.NewGuid());
            }, fail: true);

            var saved = provider.Records().Should().ContainSingle().Subject;
            saved.DataAudits.Should().ContainSingle().Which.Changes.Should().Contain("Paid");
            saved.AccessLog.Metadata["UncommittedChanges"]!.ToString().Should().Be("1");
        }

        [Fact]
        public async Task A_retry_saves_only_the_attempt_that_committed_Async()
        {
            using var provider = Build(mediatorAudit: true);
            var retry = Guid.NewGuid();

            await RunAsync(provider, async mediator =>
            {
                await ChangeAsync(mediator, "First attempt", Guid.NewGuid());
                await ChangeAsync(mediator, "Second attempt", retry);
                await CommitAsync(mediator, retry);
            });

            provider.Records().Should().ContainSingle().Which.DataAudits.Should().ContainSingle()
                .Which.Changes.Should().Contain("Second attempt");
        }

        [Fact]
        public async Task A_commit_outside_any_scope_is_ignored_Async()
        {
            using var provider = Build();
            using var scope = provider.CreateScope();

            await CommitAsync(scope.ServiceProvider.GetRequiredService<IMediator>(), Guid.NewGuid());

            provider.Records().Should().BeEmpty();
        }
    }
}
