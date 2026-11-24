using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit
{
    /// <summary>
    /// Where a data change goes when it can be saved with the business change: the outbox of the transaction that made
    /// it (registered by <c>AddAuditOutbox&lt;TContext&gt;()</c> in Juice.Audit.Messaging). A change the sink doesn't
    /// take is carried with its access record, best effort, as before.
    /// </summary>
    public interface IDataChangeSink
    {
        /// <summary>
        /// <c>true</c>: the change is in the transaction's outbox and commits or rolls back with it. <c>false</c>: the
        /// outbox can't be used (no transaction, no outbox for it, no route, another tenant, an error); the reason is
        /// counted. Never throws.
        /// </summary>
        ValueTask<bool> TryAppendAsync(Guid? transactionId, DataAudit entry, CancellationToken cancellationToken);
    }
}
