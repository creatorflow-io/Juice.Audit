namespace Juice.Audit.Export
{
    /// <summary>
    /// How access records leave the app: queued in memory, then sent to the collector in batches by a background
    /// worker. Bound from <c>Audit:Export</c>. The defaults follow OpenTelemetry's batch processor.
    /// </summary>
    public sealed class AuditExportOptions
    {
        /// <summary>Records waiting at most; a record that finds the queue full is dropped and counted.</summary>
        public int QueueCapacity { get; set; } = 10_000;

        /// <summary>A batch is sent when it has this many records…</summary>
        public int MaxBatchSize { get; set; } = 512;

        /// <summary>…or when its first record has waited this long, whichever comes first.</summary>
        public TimeSpan MaxBatchAge { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>The deadline of every export call; a call that passes it is cancelled and retried.</summary>
        public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Retries of a failed batch before its records are dropped and counted.</summary>
        public int MaxRetryAttempts { get; set; } = 5;

        /// <summary>The first retry's delay; each next one doubles, with ±20% jitter, up to <see cref="MaxBackoff"/>.</summary>
        public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>On shutdown, the queue is sent for at most this long; what is left is dropped and counted.</summary>
        public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Lost records are logged as one warning per this interval, with the count and the reasons.</summary>
        public TimeSpan DropWarningInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// <c>true</c>: a record is exported inside the scope that made it, before the scope completes, for tests that
        /// read the store right after a request. Off by default: auditing must not make requests wait.
        /// </summary>
        public bool Synchronous { get; set; }
    }
}
