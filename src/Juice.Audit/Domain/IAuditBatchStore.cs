using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit.Domain
{
    /// <summary>
    /// Writes audit records in batches, for the collector: finds the ids already stored, tracks the new records and
    /// saves them in one unit. Implemented by <c>Juice.Audit.EF</c>; kept apart from the repositories so their
    /// interfaces don't change.
    /// </summary>
    public interface IAuditBatchStore
    {
        /// <summary>The ids among <paramref name="ids"/> that are already stored as access records.</summary>
        Task<IReadOnlyCollection<Guid>> GetExistingAccessLogIdsAsync(IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken);

        /// <summary>The ids among <paramref name="ids"/> that are already stored as data changes.</summary>
        Task<IReadOnlyCollection<Guid>> GetExistingDataAuditIdsAsync(IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken);

        /// <summary>Tracks the record for the next <see cref="SaveChangesAsync"/>; nothing is written yet.</summary>
        void Add(AccessLog accessLog);

        /// <summary>Tracks the record for the next <see cref="SaveChangesAsync"/>; nothing is written yet.</summary>
        void Add(DataAudit dataAudit);

        /// <summary>Writes every tracked record.</summary>
        Task SaveChangesAsync(CancellationToken cancellationToken);

        /// <summary>Forgets the tracked records, after a failed save, so they can be retried one by one.</summary>
        void ClearTracked();
    }
}
