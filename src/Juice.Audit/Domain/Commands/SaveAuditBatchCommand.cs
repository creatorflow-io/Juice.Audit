using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit.Commands
{
    /// <summary>
    /// One access record with the data changes carried with it, or data changes alone (from the outbox, whose access
    /// record travels apart).
    /// </summary>
    public sealed record AuditBatchItem(AccessLog? AccessLog, IReadOnlyList<DataAudit> DataAudits)
    {
        public AuditBatchItem(AccessLog accessLog) : this(accessLog, Array.Empty<DataAudit>()) { }
    }

    /// <summary>
    /// Stores a batch of audit records once each: records whose id is already stored are skipped, the others are
    /// written together. The result tells, per record, whether it was stored and whether a failure can be retried.
    /// </summary>
    public sealed record SaveAuditBatchCommand(IReadOnlyList<AuditBatchItem> Items) : MessageBase, IRequest<AuditBatchResult>;

    public enum AuditRecordKind
    {
        Access,
        Data
    }

    public enum AuditRecordOutcome
    {
        Stored,
        /// <summary>Already stored: a success for the sender.</summary>
        Duplicate,
        /// <summary>Invalid or refused: retrying won't help.</summary>
        Rejected,
        /// <summary>Not stored because of a transient error: retry later.</summary>
        Retryable,
        /// <summary>
        /// Sent, but whether it was stored is unknown: the call was cut off. Only an exporter talking to a collector
        /// that can't recognize a resent record (before 10.7) gives it, as retrying could store the record twice.
        /// </summary>
        Unconfirmed
    }

    public sealed record AuditRecordResult(Guid Id, AuditRecordKind Kind, AuditRecordOutcome Outcome, string? Reason = null)
    {
        /// <summary>Stored now or before.</summary>
        public bool IsStored => Outcome is AuditRecordOutcome.Stored or AuditRecordOutcome.Duplicate;
    }

    public sealed record AuditBatchResult(IReadOnlyList<AuditRecordResult> Results)
    {
        public bool AllStored => Results.All(r => r.IsStored);

        public bool AnyRetryable => Results.Any(r => r.Outcome == AuditRecordOutcome.Retryable);

        /// <summary>A success when every record is stored; otherwise a failure listing what wasn't.</summary>
        public IOperationResult ToOperationResult()
        {
            if (AllStored)
            {
                return OperationResult.Success;
            }
            var failed = Results.Where(r => !r.IsStored)
                .Select(r => $"{r.Kind} {r.Id}: {r.Outcome}{(r.Reason != null ? " (" + r.Reason + ")" : "")}");
            return OperationResult.Failed(string.Join("; ", failed));
        }
    }
}
