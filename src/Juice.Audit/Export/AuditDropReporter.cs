using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Audit.Export
{
    /// <summary>
    /// Counts every lost access record (<c>juice.audit.export.records{outcome=dropped}</c>) and logs the losses as one
    /// warning per <see cref="AuditExportOptions.DropWarningInterval"/>, with the count and the reasons, so an outage
    /// that drops thousands of records doesn't flood the log (FR-027). The first drop opens a window; the warning is
    /// written when it closes, or at once by <see cref="Flush"/> (shutdown).
    /// </summary>
    public sealed class AuditDropReporter : IDisposable
    {
        private readonly AuditExportMetrics _metrics;
        private readonly ILogger _logger;
        private readonly TimeSpan _interval;
        private readonly object _lock = new();
        private readonly Dictionary<AuditDropReason, long> _pending = new();
        private readonly Stopwatch _window = new();
        private Timer? _timer;
        private bool _disposed;

        public AuditDropReporter(AuditExportMetrics metrics, IOptions<AuditExportOptions> options,
            ILogger<AuditDropReporter> logger)
        {
            _metrics = metrics;
            _logger = logger;
            _interval = options.Value.DropWarningInterval > TimeSpan.Zero
                ? options.Value.DropWarningInterval
                : TimeSpan.FromSeconds(30);
        }

        /// <summary>Counts <paramref name="count"/> lost records; the warning follows within the interval.</summary>
        public void Dropped(long count, AuditDropReason reason)
        {
            if (count <= 0)
            {
                return;
            }
            _metrics.Dropped(count, reason);
            lock (_lock)
            {
                _pending[reason] = _pending.TryGetValue(reason, out var sum) ? sum + count : count;
                if (_timer == null && !_disposed)
                {
                    _window.Restart();
                    _timer = new Timer(_ => Flush(), null, _interval, Timeout.InfiniteTimeSpan);
                }
            }
        }

        /// <summary>Logs the drops counted since the last warning, if any, and closes the window.</summary>
        public void Flush()
        {
            KeyValuePair<AuditDropReason, long>[] drops;
            TimeSpan elapsed;
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
                if (_pending.Count == 0)
                {
                    return;
                }
                drops = _pending.OrderBy(p => p.Key).ToArray();
                _pending.Clear();
                elapsed = _window.Elapsed;
            }
            try
            {
                var reasons = string.Join(", ", drops.Select(d => $"{AuditExportMetrics.Tag(d.Key)}: {d.Value}"));
                _logger.LogWarning("Dropped {Count} audit records in the last {Interval} ({Reasons}).",
                    drops.Sum(d => d.Value), Round(elapsed), reasons);
            }
            catch
            {
                // A logger must not take the export down.
            }
        }

        private static TimeSpan Round(TimeSpan elapsed)
            => TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(elapsed.TotalSeconds)));

        public void Dispose()
        {
            Flush();
            lock (_lock)
            {
                _disposed = true;
            }
        }
    }
}
