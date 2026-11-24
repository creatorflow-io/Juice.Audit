using Juice.Audit.Commands;

namespace Juice.Audit.Domain
{
    /// <summary>
    /// Told about every batch the collector writes (<see cref="SaveAuditBatchCommand"/>): what came in, what became of
    /// each record, and how long the write took. The collector's metrics implement it; it must not throw.
    /// </summary>
    public interface IAuditBatchObserver
    {
        void BatchWritten(IReadOnlyList<AuditBatchItem> items, AuditBatchResult result, TimeSpan elapsed);
    }
}
