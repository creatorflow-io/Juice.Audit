using System.Diagnostics;
using Grpc.Core;
using Juice.Audit.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Audit.Export
{
    /// <summary>
    /// Sends the export queue to the collector in the background: a batch goes when it is full or its first record
    /// has waited <see cref="AuditExportOptions.MaxBatchAge"/>. A failed batch is retried with backoff, then dropped and
    /// counted; the worker goes on with the next one. On shutdown, what is in flight or queued is sent for at most
    /// <see cref="AuditExportOptions.ShutdownFlushTimeout"/>; the rest is dropped and counted. Never throws.
    /// </summary>
    public sealed class AuditExportWorker : BackgroundService
    {
        private readonly AuditExportQueue _queue;
        private readonly AuditExportMetrics _metrics;
        private readonly AuditDropReporter _drops;
        private readonly IServiceScopeFactory _scopes;
        private readonly AuditExportOptions _options;
        private readonly ILogger _logger;

        /// <summary>Cancelled when the flush time after a stop request runs out.</summary>
        private readonly CancellationTokenSource _hardStop = new();

        /// <summary>1 once <see cref="ExecuteAsync"/> or the stop has taken charge of the queue.</summary>
        private int _running;

        public AuditExportWorker(AuditExportQueue queue, AuditExportMetrics metrics, AuditDropReporter drops,
            IServiceScopeFactory scopes, IOptions<AuditExportOptions> options, ILogger<AuditExportWorker> logger)
        {
            _queue = queue;
            _metrics = metrics;
            _drops = drops;
            _scopes = scopes;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                return;
            }
            // Let the host finish starting: the first await below may complete synchronously.
            await Task.Yield();
            var leftover = new List<AuditExportItem>();
            using var stopping = stoppingToken.Register(StartFlushClock);
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var batch = await ReadBatchAsync(stoppingToken);
                    if (batch.Count > 0)
                    {
                        // The call itself runs until it ends or the flush time runs out; a stop only cuts the
                        // backoff short, and whatever is left is flushed.
                        leftover.AddRange(await ExportAsync(batch, retryDelay: stoppingToken, call: _hardStop.Token));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The audit export worker stopped unexpectedly; flushing what is left.");
            }
            await FlushAsync(leftover);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            // Since .NET 10 ExecuteAsync starts with Task.Run on the stopping token: a stop right after the start
            // cancels it before it runs, and the queue would be lost uncounted. Flush it here instead.
            if (Interlocked.Exchange(ref _running, 1) == 0)
            {
                StartFlushClock();
                await FlushAsync(new List<AuditExportItem>());
            }
        }

        /// <summary>A stop was requested: the flush may take <see cref="AuditExportOptions.ShutdownFlushTimeout"/>.</summary>
        private void StartFlushClock()
        {
            try
            {
                _hardStop.CancelAfter(_options.ShutdownFlushTimeout);
            }
            catch (ObjectDisposedException)
            {
                // Disposed without a stop: nothing is left to flush to.
            }
        }

        public override void Dispose()
        {
            // The base cancels the stopping token first, which runs StartFlushClock on a live source.
            base.Dispose();
            _hardStop.Cancel();
            _hardStop.Dispose();
        }

        /// <summary>Up to MaxBatchSize records, or what arrives before the first one is MaxBatchAge old.</summary>
        private async Task<List<AuditExportItem>> ReadBatchAsync(CancellationToken stoppingToken)
        {
            var batch = new List<AuditExportItem>();
            var reader = _queue.Reader;
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken))
                {
                    return batch;
                }
                while (batch.Count < _options.MaxBatchSize)
                {
                    if (reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        continue;
                    }
                    var age = DateTimeOffset.UtcNow - batch[0].EnqueuedAt;
                    var wait = _options.MaxBatchAge - age;
                    if (wait <= TimeSpan.Zero)
                    {
                        break;
                    }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(wait);
                    try
                    {
                        if (!await reader.WaitToReadAsync(timeout.Token))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        break; // the batch is old enough
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Stopping: what was read is flushed.
            }
            return batch;
        }

        /// <summary>
        /// Exports <paramref name="batch"/>, retrying what can be retried. Returns the records still pending when
        /// <paramref name="retryDelay"/> or <paramref name="call"/> was cancelled; the others are stored or counted.
        /// An attempt that settles some records starts the count of retries over: only attempts that settle none use
        /// up <see cref="AuditExportOptions.MaxRetryAttempts"/>, so a slow collector working through a batch isn't
        /// given up on.
        /// </summary>
        private async Task<List<AuditExportItem>> ExportAsync(List<AuditExportItem> batch, CancellationToken retryDelay,
            CancellationToken call)
        {
            var pending = batch;
            // Attempts since one last settled a record, this one included.
            var stalled = 0;
            for (var attempt = 1; ; attempt++)
            {
                if (call.IsCancellationRequested)
                {
                    return pending;
                }
                var started = Stopwatch.StartNew();
                string? error = null;
                var progressed = false;
                try
                {
                    using var scope = _scopes.CreateScope();
                    var exporter = scope.ServiceProvider.GetRequiredService<IAuditExporter>();
                    var result = await exporter.ExportAsync(pending, call);
                    var before = pending.Count;
                    pending = Settle(pending, result);
                    progressed = pending.Count < before;
                    if (pending.Count > 0)
                    {
                        error = "retryable";
                    }
                }
                catch (OperationCanceledException) when (call.IsCancellationRequested)
                {
                    return pending;
                }
                catch (Exception ex)
                {
                    error = ex is RpcException rpc ? rpc.StatusCode.ToString() : ex.GetType().Name;
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(ex, "Exporting {Count} audit records failed (attempt {Attempt}).", pending.Count,
                            attempt);
                    }
                }
                _metrics.ExportDuration(started.Elapsed, error == null);
                if (error == null)
                {
                    return new List<AuditExportItem>();
                }

                _metrics.BatchFailed(error);
                stalled = progressed ? 1 : stalled + 1;
                if (stalled > _options.MaxRetryAttempts)
                {
                    _drops.Dropped(pending.Count, AuditDropReason.RetriesExhausted);
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Dropped {Count} audit records after {Attempts} export attempts ({Error}).",
                            pending.Count, attempt, error);
                    }
                    return new List<AuditExportItem>();
                }
                _metrics.Retried();
                try
                {
                    await Task.Delay(Backoff(stalled), retryDelay);
                }
                catch (OperationCanceledException)
                {
                    return pending;
                }
            }
        }

        /// <summary>Counts what the collector stored or refused; returns the records to retry.</summary>
        private List<AuditExportItem> Settle(List<AuditExportItem> batch, AuditBatchResult result)
        {
            var outcomes = new Dictionary<Guid, AuditRecordResult>();
            foreach (var record in result.Results)
            {
                outcomes[record.Id] = record;
            }
            var retry = new List<AuditExportItem>();
            long exported = 0, duplicate = 0, rejected = 0, unconfirmed = 0;
            foreach (var item in batch)
            {
                // A record the collector didn't answer for is retried: stored records come back as duplicates.
                if (item.RecordIds.Any(id => !outcomes.TryGetValue(id, out var r) || r.Outcome == AuditRecordOutcome.Retryable))
                {
                    retry.Add(item);
                    continue;
                }
                var access = outcomes[item.AccessRecord.Id];
                switch (access.Outcome)
                {
                    case AuditRecordOutcome.Stored:
                        exported++;
                        break;
                    case AuditRecordOutcome.Duplicate:
                        duplicate++;
                        break;
                    case AuditRecordOutcome.Unconfirmed:
                        unconfirmed++;
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Audit record {Id} may not have been stored, and is not resent: {Reason}",
                                access.Id, access.Reason);
                        }
                        break;
                    default:
                        rejected++;
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("The collector refused audit record {Id}: {Reason}", access.Id, access.Reason);
                        }
                        break;
                }
                if (access.Outcome is AuditRecordOutcome.Stored or AuditRecordOutcome.Duplicate)
                {
                    // The access record went in, but a data change carried with it can still be refused: it is lost
                    // as surely as a refused access record, so it is counted the same way.
                    foreach (var entry in item.BestEffortEntries)
                    {
                        var data = outcomes[entry.Id];
                        if (data.Outcome is AuditRecordOutcome.Stored or AuditRecordOutcome.Duplicate)
                        {
                            continue;
                        }
                        rejected++;
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("The collector refused data change {Id}: {Reason}", data.Id, data.Reason);
                        }
                    }
                }
            }
            _metrics.Exported(exported);
            _metrics.Duplicate(duplicate);
            _drops.Dropped(rejected, AuditDropReason.Rejected);
            _drops.Dropped(unconfirmed, AuditDropReason.Unconfirmed);
            return retry;
        }

        /// <summary>Sends what is left until the flush time runs out; drops and counts the rest.</summary>
        private async Task FlushAsync(List<AuditExportItem> leftover)
        {
            var pending = new Queue<AuditExportItem>(leftover);
            while (_queue.Reader.TryRead(out var item))
            {
                pending.Enqueue(item);
            }
            try
            {
                while (pending.Count > 0 && !_hardStop.IsCancellationRequested)
                {
                    var batch = new List<AuditExportItem>();
                    while (batch.Count < _options.MaxBatchSize && pending.TryDequeue(out var next))
                    {
                        batch.Add(next);
                    }
                    foreach (var unsent in await ExportAsync(batch, retryDelay: _hardStop.Token, call: _hardStop.Token))
                    {
                        pending.Enqueue(unsent);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flushing the audit export queue failed.");
            }
            // Anything enqueued during the flush is lost too.
            while (_queue.Reader.TryRead(out var late))
            {
                pending.Enqueue(late);
            }
            if (pending.Count > 0)
            {
                _drops.Dropped(pending.Count, AuditDropReason.Shutdown);
            }
            // The host is going away: write the drop warning now, not at the end of its interval.
            _drops.Flush();
        }

        private TimeSpan Backoff(int attempt)
        {
            var delay = _options.InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
            delay = Math.Min(delay, _options.MaxBackoff.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(delay * (0.8 + 0.4 * Random.Shared.NextDouble()));
        }
    }
}
