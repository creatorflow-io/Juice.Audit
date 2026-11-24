using Juice.Audit.Commands;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit.Export
{
    /// <summary>An access record with the data changes carried with it (the ones the outbox couldn't take).</summary>
    public sealed class AuditExportItem
    {
        public AuditExportItem(AccessLog accessRecord, DataAudit[] bestEffortEntries)
        {
            AccessRecord = accessRecord;
            BestEffortEntries = bestEffortEntries;
            EnqueuedAt = DateTimeOffset.UtcNow;
        }

        public AccessLog AccessRecord { get; }
        public DataAudit[] BestEffortEntries { get; }
        public DateTimeOffset EnqueuedAt { get; }

        /// <summary>The ids of every record in the item.</summary>
        internal IEnumerable<Guid> RecordIds => BestEffortEntries.Select(e => e.Id).Prepend(AccessRecord.Id);
    }

    /// <summary>
    /// Sends a batch of records to the collector: over gRPC (remote mode) or to the mediator (in-process mode). It owns
    /// the deadline of the call (<see cref="AuditExportOptions.ExportTimeout"/>). It returns, per record, whether it
    /// was stored; it throws when the whole call failed, and the batch is retried.
    /// </summary>
    public interface IAuditExporter
    {
        Task<AuditBatchResult> ExportAsync(IReadOnlyList<AuditExportItem> batch, CancellationToken cancellationToken);
    }
}
