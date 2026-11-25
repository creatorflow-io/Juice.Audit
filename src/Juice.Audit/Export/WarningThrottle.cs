using System.Diagnostics;

namespace Juice.Audit.Export
{
    /// <summary>
    /// Lets a warning through at most once per interval, on the monotonic clock: a wall clock set back would otherwise
    /// silence it until it caught up.
    /// </summary>
    internal sealed class WarningThrottle
    {
        private readonly long _interval;
        private long _last;

        public WarningThrottle(TimeSpan interval)
        {
            _interval = (long)(interval.TotalSeconds * Stopwatch.Frequency);
            _last = Stopwatch.GetTimestamp() - _interval; // the first warning goes through
        }

        /// <summary><c>true</c> when the interval has passed since the last warning; one caller wins a race.</summary>
        public bool ShouldWarn()
        {
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _last);
            return now - last >= _interval && Interlocked.CompareExchange(ref _last, now, last) == last;
        }
    }
}
