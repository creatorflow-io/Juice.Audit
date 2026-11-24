using Grpc.Core;
using Juice.Audit.Commands;
using Juice.Audit.Export;
using Juice.Audit.Grpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuditBatchResult = Juice.Audit.Commands.AuditBatchResult;
using RecordResult = Juice.Audit.Commands.AuditRecordResult;

namespace Juice.Audit.Services
{
    /// <summary>What the gRPC exporter learned about the collector, for the life of the process.</summary>
    internal sealed class GrpcExportState
    {
        /// <summary>
        /// How long records go one by one before <c>LogBatch</c> is tried again: one <c>Unimplemented</c> may come from
        /// an old instance in a rolling upgrade, or a proxy, and one-by-one calls can't be resent safely.
        /// </summary>
        public static readonly TimeSpan DefaultRetryBatchAfter = TimeSpan.FromMinutes(5);

        private readonly TimeSpan _retryBatchAfter;
        private long _unaryUntil;
        private int _switched;

        public GrpcExportState() : this(DefaultRetryBatchAfter)
        {
        }

        internal GrpcExportState(TimeSpan retryBatchAfter) => _retryBatchAfter = retryBatchAfter;

        /// <summary>The collector had no LogBatch (before 10.7) a while ago: records go one by one.</summary>
        public bool UseUnary => DateTime.UtcNow.Ticks < Interlocked.Read(ref _unaryUntil);

        /// <summary>
        /// Records go one by one for a while (<see cref="DefaultRetryBatchAfter"/>). <c>true</c> the first time only,
        /// so the switch is logged once.
        /// </summary>
        public bool SwitchToUnary()
        {
            Interlocked.Exchange(ref _unaryUntil, DateTime.UtcNow.Add(_retryBatchAfter).Ticks);
            return Interlocked.Exchange(ref _switched, 1) == 0;
        }
    }

    /// <summary>
    /// Remote mode: sends a batch with <c>LogBatch</c>, under the deadline
    /// <see cref="AuditExportOptions.ExportTimeout"/>, so a collector that hangs never holds the worker longer. A
    /// collector before 10.7 answers <c>Unimplemented</c>: the exporter switches to the unary <c>Log</c>, one record
    /// per call, and logs it once. Such a collector stores a resent record again, so a record it may have stored is
    /// never resent (see <see cref="ExportOneByOneAsync"/>).
    /// </summary>
    internal sealed class GrpcAuditExporter : IAuditExporter
    {
        public const string MinimumCollectorVersion = "10.7";

        private readonly AuditGrpcService.AuditGrpcServiceClient _client;
        private readonly GrpcExportState _state;
        private readonly AuditExportOptions _options;
        private readonly ILogger _logger;

        public GrpcAuditExporter(AuditGrpcService.AuditGrpcServiceClient client, GrpcExportState state,
            IOptions<AuditExportOptions> options, ILogger<GrpcAuditExporter> logger)
        {
            _client = client;
            _state = state;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<AuditBatchResult> ExportAsync(IReadOnlyList<AuditExportItem> batch,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.Add(_options.ExportTimeout);
            if (!_state.UseUnary)
            {
                var request = new AuditBatchRequest();
                request.Items.AddRange(batch.Select(i => AuditMessageMapper.ToMessage(i.AccessRecord, i.BestEffortEntries)));
                try
                {
                    var reply = await _client.LogBatchAsync(request, deadline: deadline,
                        cancellationToken: cancellationToken);
                    return FromReply(batch, reply);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
                {
                    if (_state.SwitchToUnary())
                    {
                        _logger.LogWarning("The audit collector has no LogBatch: sending one record per call. " +
                            "Upgrade the collector to Juice.Audit {Version} or later.", MinimumCollectorVersion);
                    }
                }
            }
            return await ExportOneByOneAsync(batch, deadline, cancellationToken);
        }

        /// <summary>
        /// A collector before 10.7 gives every record it receives a new id, so it can't tell a resent record from a new
        /// one, and it saves a record's data changes before its access record. Resending what it may have stored would
        /// store it again: as before 10.7, a record it was sent is not sent again, except when it surely never got it.
        /// <list type="bullet">
        /// <item><c>Succeeded = false</c>: refused (<see cref="AuditRecordOutcome.Rejected"/>). The collector gives
        /// every error so, and its data changes may already be stored.</item>
        /// <item>The collector can't be reached (<see cref="StatusCode.Unavailable"/>): retried, with the rest.</item>
        /// <item>The call is cut off (its deadline, a reset): <see cref="AuditRecordOutcome.Unconfirmed"/>; the rest
        /// is retried.</item>
        /// </list>
        /// Each call has its own deadline, so a slow collector is never cut off mid-write: once
        /// <paramref name="budget"/> is spent no more calls start, and the rest goes in the next attempt.
        /// </summary>
        private async Task<AuditBatchResult> ExportOneByOneAsync(IReadOnlyList<AuditExportItem> batch, DateTime budget,
            CancellationToken cancellationToken)
        {
            var results = new List<RecordResult>();
            string? stopped = null;
            foreach (var item in batch)
            {
                AuditRecordOutcome outcome;
                string? reason;
                if (stopped == null && DateTime.UtcNow >= budget)
                {
                    stopped = "the export time of the batch is spent";
                }
                if (stopped != null)
                {
                    outcome = AuditRecordOutcome.Retryable;
                    reason = stopped;
                }
                else
                {
                    try
                    {
                        var reply = await _client.LogAsync(
                            AuditMessageMapper.ToMessage(item.AccessRecord, item.BestEffortEntries),
                            deadline: DateTime.UtcNow.Add(_options.ExportTimeout), cancellationToken: cancellationToken);
                        outcome = reply.Succeeded ? AuditRecordOutcome.Stored : AuditRecordOutcome.Rejected;
                        reason = reply.Succeeded ? null : reply.Message;
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable
                        || cancellationToken.IsCancellationRequested)
                    {
                        // Not received, or stopping (the worker counts what is left as lost to the shutdown).
                        stopped = ex.Status.Detail;
                        outcome = AuditRecordOutcome.Retryable;
                        reason = stopped;
                    }
                    catch (RpcException ex)
                    {
                        // Received, perhaps stored: not sent again. The collector is in trouble; the rest waits.
                        stopped = ex.Status.Detail;
                        outcome = AuditRecordOutcome.Unconfirmed;
                        reason = $"{ex.StatusCode}: {ex.Status.Detail}";
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Not the transport's status (the channel, the handler): perhaps sent, so not resent either.
                        // Thrown on, it would lose the answers of the records already stored, and they would be resent.
                        stopped = ex.Message;
                        outcome = AuditRecordOutcome.Unconfirmed;
                        reason = $"{ex.GetType().Name}: {ex.Message}";
                    }
                }
                results.Add(new RecordResult(item.AccessRecord.Id, AuditRecordKind.Access, outcome, reason));
                results.AddRange(item.BestEffortEntries.Select(e =>
                    new RecordResult(e.Id, AuditRecordKind.Data, outcome, reason)));
            }
            return new AuditBatchResult(results);
        }

        private static AuditBatchResult FromReply(IReadOnlyList<AuditExportItem> batch, Juice.Audit.Grpc.AuditBatchResult reply)
        {
            var kinds = new Dictionary<Guid, AuditRecordKind>();
            foreach (var item in batch)
            {
                kinds[item.AccessRecord.Id] = AuditRecordKind.Access;
                foreach (var entry in item.BestEffortEntries)
                {
                    kinds[entry.Id] = AuditRecordKind.Data;
                }
            }
            var results = new List<RecordResult>(reply.Results.Count);
            foreach (var r in reply.Results)
            {
                if (Guid.TryParse(r.Id, out var id))
                {
                    results.Add(new RecordResult(id, kinds.GetValueOrDefault(id), AuditMessageMapper.FromMessage(r.Outcome),
                        r.Reason));
                }
            }
            return new AuditBatchResult(results);
        }
    }
}
