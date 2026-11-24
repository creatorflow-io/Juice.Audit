using Juice.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Messaging.Outbox
{
    /// <summary>An audited context whose transactions can carry their data changes in their outbox.</summary>
    internal interface IAuditOutboxBinding
    {
        Type ContextType { get; }

        /// <summary>The scope's context runs <paramref name="transactionId"/>.</summary>
        bool Runs(Guid transactionId);

        /// <summary>
        /// <c>TransactionBehavior</c> manages the context (<see cref="IManagable.IsManaged"/>): the only caller that
        /// saves the outbox. A transaction the app opens itself would leave the event unsaved.
        /// </summary>
        bool IsManaged { get; }

        /// <summary>That context's outbox, the one <c>TransactionBehavior</c> saves before it commits.</summary>
        IOutboxService Outbox { get; }
    }

    /// <summary>
    /// Core doesn't tell which context raised a data change, only its transaction id. The scoped
    /// <typeparamref name="TContext"/> is the instance <c>TransactionBehavior</c> manages, so comparing its current
    /// transaction finds the right outbox.
    /// </summary>
    internal sealed class AuditOutboxBinding<TContext> : IAuditOutboxBinding
        where TContext : DbContext
    {
        private readonly IServiceProvider _services;

        public AuditOutboxBinding(IServiceProvider services)
        {
            _services = services;
        }

        public Type ContextType => typeof(TContext);

        public bool Runs(Guid transactionId)
            => _services.GetService<TContext>()?.Database.CurrentTransaction?.TransactionId == transactionId;

        public bool IsManaged => _services.GetService<TContext>() is IManagable { IsManaged: true };

        public IOutboxService Outbox => _services.GetRequiredService<IOutboxService<TContext>>();
    }
}
