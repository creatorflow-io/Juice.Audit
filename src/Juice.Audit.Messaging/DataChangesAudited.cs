using Juice.Messaging;
using Juice.Messaging.Attributes;

namespace Juice.Audit.Messaging
{
    /// <summary>
    /// The audited data changes of one business transaction, saved in its outbox with the change itself, so they reach
    /// the audit service if and only if the transaction committed. A transaction over the size limits is split into
    /// several parts. Route domain <c>juice.audit</c> in the publishing policy to deliver it.
    /// </summary>
    [Domain("juice.audit")]
    public sealed record DataChangesAudited : IntegrationEvent
    {
        /// <summary>The app that made the changes.</summary>
        public string? App { get; set; }

        /// <summary>The business transaction.</summary>
        public Guid TransactionId { get; set; }

        /// <summary>The scope's tenant; also the outbox's <c>x-tenant-id</c>.</summary>
        public string? TenantId { get; set; }

        /// <summary>1 for the first part of a transaction's changes, then 2, 3…</summary>
        public int Part { get; set; } = 1;

        public List<DataChangeEntry> Entries { get; set; } = new();

        /// <summary>
        /// Set by the app's outbox sink: whether <see cref="TransactionId"/> is the scope's transaction now.
        /// </summary>
        internal Func<bool>? IsInTransaction { get; set; }

        /// <summary>
        /// The outbox serializes its pending events when <c>TransactionBehavior</c> saves them. An event whose
        /// transaction rolled back is still pending, and is saved by the scope's next transaction: its entries are left
        /// out then (Newtonsoft calls this by name), so changes that never committed are never stored.
        /// </summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public bool ShouldSerializeEntries()
        {
            try
            {
                return IsInTransaction?.Invoke() ?? true;
            }
            catch (ObjectDisposedException)
            {
                return true; // serialized after its scope: not by the outbox
            }
        }
    }

    /// <summary>One changed row: the fields of a stored data change, with the id given in the app.</summary>
    public sealed class DataChangeEntry
    {
        public Guid Id { get; set; }
        public string? User { get; set; }
        public DateTimeOffset DateTime { get; set; }
        public string Action { get; set; } = "";
        public string? Db { get; set; }
        public string? Schema { get; set; }
        public string Tbl { get; set; } = "";
        /// <summary>The key values, as JSON.</summary>
        public string Kvps { get; set; } = "{}";
        /// <summary>The original and current values, as JSON.</summary>
        public string Changes { get; set; } = "{}";
        public string? TraceId { get; set; }
        public string? TenantId { get; set; }
    }
}
