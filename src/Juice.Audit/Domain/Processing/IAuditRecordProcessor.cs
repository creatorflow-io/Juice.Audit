using Juice.Audit.Commands;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit.Collector
{
    /// <summary>
    /// Runs in the collector on every new record before it is stored: to mask, cut, enrich or refuse it. Processors run
    /// in <see cref="Order"/> ascending and change the record in place. A processor that throws or rejects affects only
    /// that record (FR-018); a duplicate is never processed. Register one with <c>AddAuditCollector().AddProcessor&lt;T&gt;()</c>.
    /// </summary>
    public interface IAuditRecordProcessor
    {
        /// <summary>Lower runs first. The built-ins: header redaction 100, truncation 200.</summary>
        int Order { get; }

        ValueTask<ProcessorResult> ProcessAsync(AuditRecordEnvelope record, CancellationToken cancellationToken);
    }

    /// <summary>One record on its way to the audit database: an access record or a data change.</summary>
    public sealed class AuditRecordEnvelope
    {
        public AuditRecordEnvelope(AccessLog access)
        {
            Kind = AuditRecordKind.Access;
            Access = access;
        }

        public AuditRecordEnvelope(DataAudit data)
        {
            Kind = AuditRecordKind.Data;
            Data = data;
        }

        public AuditRecordKind Kind { get; }

        /// <summary>Set when <see cref="Kind"/> is <see cref="AuditRecordKind.Access"/>.</summary>
        public AccessLog? Access { get; }

        /// <summary>Set when <see cref="Kind"/> is <see cref="AuditRecordKind.Data"/>.</summary>
        public DataAudit? Data { get; }

        public Guid Id => Access?.Id ?? Data!.Id;

        /// <summary>Scratch space shared by the processors of this record; not stored.</summary>
        public Dictionary<string, object?> Notes { get; } = new();
    }

    /// <summary>What a processor decided: go on, or refuse the record.</summary>
    public readonly struct ProcessorResult
    {
        private ProcessorResult(string? rejection) => Rejection = rejection;

        /// <summary>The record goes on to the next processor, then to the database.</summary>
        public static ProcessorResult Continue => default;

        /// <summary>The record is not stored; the sender is told it was rejected, and doesn't retry it.</summary>
        public static ProcessorResult Reject(string reason) => new(reason);

        public bool IsRejected => Rejection != null;

        public string? Rejection { get; }
    }
}
