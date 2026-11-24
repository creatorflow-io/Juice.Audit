using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Juice.Audit.Export
{
    /// <summary>
    /// The app side instruments of meter <c>Juice.Audit</c>: the export queue and the outbox hand-off. Collect them with
    /// <c>.WithMetrics(m =&gt; m.AddMeter("Juice.Audit"))</c>. Every loss of a record is counted here, with a reason.
    /// </summary>
    public sealed class AuditExportMetrics : IDisposable
    {
        public const string MeterName = "Juice.Audit";

        private readonly Counter<long> _records;
        private readonly Counter<long> _retries;
        private readonly Counter<long> _failedBatches;
        private readonly Histogram<double> _duration;
        private readonly Counter<long> _outboxEntries;
        private Func<long> _queueSize = () => 0;
        private long _queueCapacity;

        public AuditExportMetrics()
        {
            Meter = new Meter(MeterName, typeof(AuditExportMetrics).Assembly.GetName().Version?.ToString());
            Meter.CreateObservableGauge("juice.audit.export.queue.size", () => _queueSize(), "{record}",
                "Access records waiting in the export queue.");
            Meter.CreateObservableGauge("juice.audit.export.queue.capacity", () => _queueCapacity, "{record}",
                "The export queue's capacity.");
            _records = Meter.CreateCounter<long>("juice.audit.export.records", "{record}",
                "Access records by outcome: exported, duplicate or dropped (with a reason).");
            _retries = Meter.CreateCounter<long>("juice.audit.export.retries", "{batch}", "Export batches retried.");
            _failedBatches = Meter.CreateCounter<long>("juice.audit.export.batches.failed", "{batch}",
                "Export calls that failed, by error.");
            _duration = Meter.CreateHistogram<double>("juice.audit.export.duration", "s", "Export call duration.");
            _outboxEntries = Meter.CreateCounter<long>("juice.audit.outbox.entries", "{record}",
                "Data changes sent through the outbox, or carried best effort (with a reason).");
        }

        /// <summary>This instance's meter; tests listen to it alone.</summary>
        public Meter Meter { get; }

        internal void ObserveQueue(Func<long> size, long capacity)
        {
            _queueSize = size;
            _queueCapacity = capacity;
        }

        internal void Exported(long count) => Records(count, "exported");

        internal void Duplicate(long count) => Records(count, "duplicate");

        internal void Dropped(long count, AuditDropReason reason)
        {
            if (count > 0)
            {
                _records.Add(count, new TagList { { "outcome", "dropped" }, { "reason", Tag(reason) } });
            }
        }

        internal void Retried() => _retries.Add(1);

        internal void BatchFailed(string error) => _failedBatches.Add(1, new TagList { { "error", error } });

        internal void ExportDuration(TimeSpan elapsed, bool success)
            => _duration.Record(elapsed.TotalSeconds, new TagList { { "outcome", success ? "success" : "failure" } });

        internal void Outboxed(long count = 1)
            => _outboxEntries.Add(count, new TagList { { "outcome", "outboxed" } });

        internal void BestEffort(AuditBestEffortReason reason, long count = 1)
            => _outboxEntries.Add(count, new TagList { { "outcome", "best_effort" }, { "reason", Tag(reason) } });

        private void Records(long count, string outcome)
        {
            if (count > 0)
            {
                _records.Add(count, new TagList { { "outcome", outcome } });
            }
        }

        internal static string Tag(AuditDropReason reason) => reason switch
        {
            AuditDropReason.QueueFull => "queue_full",
            AuditDropReason.RetriesExhausted => "retries_exhausted",
            AuditDropReason.Shutdown => "shutdown",
            AuditDropReason.Rejected => "rejected",
            AuditDropReason.Unconfirmed => "unconfirmed",
            _ => "unknown"
        };

        internal static string Tag(AuditBestEffortReason reason) => reason switch
        {
            AuditBestEffortReason.NoTransaction => "no_transaction",
            AuditBestEffortReason.NoBinding => "no_binding",
            AuditBestEffortReason.NoRoute => "no_route",
            AuditBestEffortReason.TenantMismatch => "tenant_mismatch",
            AuditBestEffortReason.Unmanaged => "unmanaged",
            _ => "error"
        };

        public void Dispose() => Meter.Dispose();
    }

    /// <summary>Why an access record was lost.</summary>
    public enum AuditDropReason
    {
        QueueFull,
        RetriesExhausted,
        Shutdown,
        Rejected,
        /// <summary>
        /// Sent to a collector before 10.7, which may or may not have stored it
        /// (<see cref="Commands.AuditRecordOutcome.Unconfirmed"/>).
        /// </summary>
        Unconfirmed
    }

    /// <summary>Why a data change went with its access record instead of the transaction's outbox.</summary>
    public enum AuditBestEffortReason
    {
        NoTransaction,
        NoBinding,
        NoRoute,
        TenantMismatch,
        Error,
        /// <summary>The transaction is the app's own, not <c>TransactionBehavior</c>'s: nothing would save the outbox.</summary>
        Unmanaged
    }
}
