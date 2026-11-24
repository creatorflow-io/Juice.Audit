namespace Juice.Audit.Messaging.Outbox
{
    /// <summary>How data changes go into a transaction's outbox. Bound from <c>Audit:Outbox</c>.</summary>
    public sealed class AuditOutboxOptions
    {
        /// <summary>The app name on each event; the entry assembly's name by default.</summary>
        public string? AppName { get; set; }

        /// <summary>An event holds at most this many changes; more start another part.</summary>
        public int MaxEntriesPerEvent { get; set; } = 500;

        /// <summary>
        /// An event stays under this many bytes as the outbox stores and publishes it: serialized, with every field,
        /// its type names and its changes escaped again as JSON strings. Half the strictest common broker limit (NATS,
        /// 1 MiB) by default; up to that limit is safe. A single change over it is truncated and marked, never dropped.
        /// </summary>
        public int MaxEventBytes { get; set; } = 512 * 1024;

        /// <summary>
        /// <c>true</c>: only changes made inside an audit scope are sent, as before 10.7. <c>false</c> (the default):
        /// any change made under <c>TransactionBehavior</c> is, background jobs included.
        /// </summary>
        public bool RequireScope { get; set; }
    }
}
