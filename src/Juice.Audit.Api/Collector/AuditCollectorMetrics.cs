using System.Diagnostics;
using System.Diagnostics.Metrics;
using Juice.Audit.Commands;
using Juice.Audit.Domain;

namespace Juice.Audit.Collector
{
    /// <summary>
    /// The collector's instruments of meter <c>Juice.Audit</c> (FR-026), recorded for every batch written by
    /// <see cref="SaveAuditBatchCommand"/>, whether it came over gRPC, from the outbox or from the app itself:
    /// <c>juice.audit.collector.records</c>, <c>.write.duration</c> and <c>.batch.size</c>, each by <c>kind</c>.
    /// </summary>
    public sealed class AuditCollectorMetrics : IAuditBatchObserver, IDisposable
    {
        public const string MeterName = "Juice.Audit";

        private readonly Counter<long> _records;
        private readonly Histogram<double> _duration;
        private readonly Histogram<long> _batchSize;

        public AuditCollectorMetrics()
        {
            Meter = new Meter(MeterName, typeof(AuditCollectorMetrics).Assembly.GetName().Version?.ToString());
            _records = Meter.CreateCounter<long>("juice.audit.collector.records", "{record}",
                "Audit records the collector received, and what became of them: stored, duplicate, rejected or failed.");
            _duration = Meter.CreateHistogram<double>("juice.audit.collector.write.duration", "s",
                "Time to write a batch to the audit database.");
            _batchSize = Meter.CreateHistogram<long>("juice.audit.collector.batch.size", "{record}",
                "Records per written batch.");
        }

        /// <summary>This instance's meter; tests listen to it alone.</summary>
        public Meter Meter { get; }

        public void BatchWritten(IReadOnlyList<AuditBatchItem> items, AuditBatchResult result, TimeSpan elapsed)
        {
            var access = items.Count(i => i.AccessLog != null);
            var data = items.Sum(i => i.DataAudits.Count);
            Batch(AuditRecordKind.Access, access, elapsed);
            Batch(AuditRecordKind.Data, data, elapsed);

            foreach (var group in result.Results.GroupBy(r => (r.Kind, r.Outcome)))
            {
                _records.Add(group.Count(), new TagList { { "kind", Kind(group.Key.Kind) }, { "outcome", Outcome(group.Key.Outcome) } });
            }
        }

        private void Batch(AuditRecordKind kind, int count, TimeSpan elapsed)
        {
            if (count == 0)
            {
                return;
            }
            var tags = new TagList { { "kind", Kind(kind) } };
            _records.Add(count, new TagList { { "kind", Kind(kind) }, { "outcome", "received" } });
            _batchSize.Record(count, tags);
            _duration.Record(elapsed.TotalSeconds, tags);
        }

        private static string Kind(AuditRecordKind kind) => kind == AuditRecordKind.Access ? "access" : "data";

        private static string Outcome(AuditRecordOutcome outcome) => outcome switch
        {
            AuditRecordOutcome.Stored => "stored",
            AuditRecordOutcome.Duplicate => "duplicate",
            AuditRecordOutcome.Rejected => "rejected",
            _ => "failed"
        };

        public void Dispose() => Meter.Dispose();
    }
}
