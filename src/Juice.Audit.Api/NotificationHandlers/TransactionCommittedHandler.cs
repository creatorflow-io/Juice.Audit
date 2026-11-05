using Juice.Domain.Events;

namespace Juice.Audit.Api.NotificationHandlers
{
    /// <summary>The changes held for this transaction are durable: they belong on the audit record now.</summary>
    internal class TransactionCommittedHandler : INotificationHandler<TransactionCommitted>
    {
        private readonly IAuditContextAccessor _auditContextAccessor;

        public TransactionCommittedHandler(IAuditContextAccessor auditContextAccessor)
        {
            _auditContextAccessor = auditContextAccessor;
        }

        public ValueTask Handle(TransactionCommitted notification, CancellationToken cancellationToken)
        {
            if (_auditContextAccessor.IsInitialized)
            {
                _auditContextAccessor.AuditContext.CommitAuditEntries(notification.TransactionId);
            }
            return ValueTask.CompletedTask;
        }
    }
}
