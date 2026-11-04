using Juice.Audit.Api.Scopes;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Domain.Events;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Messaging.Idempotency;
using Juice.Messaging.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// What <c>ConfigureAuditDefault()</c> registers, with the store replaced by a recorder, plus a mediator, messaging
    /// and handlers that change data the way a DbContext reports it: an <see cref="AuditEvent{T}"/> published on the
    /// mediator.
    /// </summary>
    internal static class AuditScopeFixture
    {
        public static ServiceProvider Build(bool messageAudit = false, bool mediatorAudit = false,
            Action<AuditScopeOptions>? configure = default)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediatR();
            services.AddMessaging();
            services.AddScoped<IIdempotencyService, AlwaysNew>();
            services.AddAuditContextAccessor().AddDataEventHandler();
            services.AddSingleton<SavedAudits>();
            services.AddScoped<IAuditService, RecordingAuditService>();
            if (messageAudit)
            {
                services.AddMessageAudit(configure);
            }
            if (mediatorAudit)
            {
                services.AddMediatorAudit(configure);
            }
            services.AddScoped<IRequestHandler<ChangeOrder>, ChangeOrderHandler>();
            services.AddScoped<INotificationHandler<OrderShipped>, OrderShippedHandler>();
            services.AddScoped<ChangingHandler>();
            services.AddScoped<SendingHandler>();
            services.AddScoped<ReadingHandler>();
            services.AddScoped<ChangingThenFailingHandler>();
            return services.BuildServiceProvider();
        }

        public static List<Saved> Records(this IServiceProvider provider) => provider.GetRequiredService<SavedAudits>().Records;

        /// <summary>The change a DbContext reports on SaveChanges.</summary>
        public static ValueTask ChangeAsync(IMediator mediator, string status = "Paid")
        {
            var change = new AuditEvent<Order>("Modified");
            change.SetAuditRecord(new AuditRecord("Orders")
            {
                KeyValues = { ["Id"] = 1 },
                CurrentValues = { ["Status"] = status }
            });
            return mediator.Publish(change);
        }

        public sealed record OrderPlaced : IntegrationEvent;

        public sealed class Order;

        public sealed record ChangeOrder(bool Fail = false) : MessageBase, IRequest;

        public sealed record OrderShipped : MessageBase, INotification;

        public sealed class ChangeOrderHandler(IMediator mediator) : IRequestHandler<ChangeOrder>
        {
            public async ValueTask Handle(ChangeOrder request, CancellationToken cancellationToken)
            {
                await ChangeAsync(mediator);
                if (request.Fail)
                {
                    throw new InvalidOperationException("order is locked");
                }
            }
        }

        public sealed class OrderShippedHandler(IMediator mediator) : INotificationHandler<OrderShipped>
        {
            public ValueTask Handle(OrderShipped notification, CancellationToken cancellationToken)
                => ChangeAsync(mediator, "Shipped");
        }

        /// <summary>Changes data directly, without a command.</summary>
        public class ChangingHandler(IMediator mediator) : IIntegrationEventHandler<OrderPlaced>
        {
            public virtual async Task HandleAsync(OrderPlaced @event) => await ChangeAsync(mediator);
        }

        public sealed class ChangingThenFailingHandler(IMediator mediator) : ChangingHandler(mediator)
        {
            public override async Task HandleAsync(OrderPlaced @event)
            {
                await base.HandleAsync(@event);
                throw new InvalidOperationException("payment refused");
            }
        }

        /// <summary>The usual Juice consumer: sends a command.</summary>
        public sealed class SendingHandler(IMediator mediator) : IIntegrationEventHandler<OrderPlaced>
        {
            public async Task HandleAsync(OrderPlaced @event) => await mediator.Send(new ChangeOrder());
        }

        public sealed class ReadingHandler : IIntegrationEventHandler<OrderPlaced>
        {
            public Task HandleAsync(OrderPlaced @event) => Task.CompletedTask;
        }

        private sealed class AlwaysNew : IIdempotencyService
        {
            public ValueTask<IdempotencyResult> TryBeginRequestAsync(string scope, string key, string? requestHash = null,
                CancellationToken cancellationToken = default)
                => ValueTask.FromResult(new IdempotencyResult(IdempotencyOutcome.Created));

            public ValueTask TryCompleteRequestAsync(string scope, string key, bool success, object? result = null,
                CancellationToken cancellationToken = default)
                => ValueTask.CompletedTask;
        }

        public sealed record Saved(AccessLog AccessLog, DataAudit[] DataAudits);

        private sealed class SavedAudits
        {
            public List<Saved> Records { get; } = new();
        }

        private sealed class RecordingAuditService(SavedAudits saved) : IAuditService
        {
            public Task<IOperationResult> PersistAuditInformationAsync(AccessLog accessLog, DataAudit[] auditEntries,
                CancellationToken token)
            {
                saved.Records.Add(new Saved(accessLog, auditEntries));
                return Task.FromResult<IOperationResult>(OperationResult.Success);
            }
        }
    }
}
