using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Juice.Audit.Export
{
    /// <summary>
    /// The bounded in-memory queue between the scopes that make access records and the export worker. Enqueuing never
    /// waits: a record that finds the queue full is dropped and counted, and the records already queued are kept, so
    /// the oldest are sent first.
    /// </summary>
    public sealed class AuditExportQueue
    {
        private readonly Channel<AuditExportItem> _channel;
        private readonly AuditDropReporter _drops;

        public AuditExportQueue(IOptions<AuditExportOptions> options, AuditExportMetrics metrics, AuditDropReporter drops)
        {
            var capacity = Math.Max(1, options.Value.QueueCapacity);
            // Wait mode, not DropWrite: TryWrite then returns false when full instead of dropping silently, so the
            // drop can be counted. Nothing ever awaits WriteAsync, so no caller waits.
            _channel = Channel.CreateBounded<AuditExportItem>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });
            _drops = drops;
            metrics.ObserveQueue(() => _channel.Reader.Count, capacity);
        }

        /// <summary>Records waiting.</summary>
        public int Count => _channel.Reader.Count;

        /// <summary>Queues <paramref name="item"/>, or drops it and counts it when the queue is full.</summary>
        public bool TryEnqueue(AuditExportItem item)
        {
            if (_channel.Writer.TryWrite(item))
            {
                return true;
            }
            _drops.Dropped(1, AuditDropReason.QueueFull);
            return false;
        }

        internal ChannelReader<AuditExportItem> Reader => _channel.Reader;
    }
}
