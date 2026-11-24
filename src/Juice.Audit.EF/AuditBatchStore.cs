using Juice.Audit.Domain;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Microsoft.EntityFrameworkCore;

namespace Juice.Audit.EF
{
    /// <summary>
    /// <see cref="IAuditBatchStore"/> on <see cref="AuditDbContext"/>. Ids are looked up in chunks, so a large batch
    /// stays under the providers' parameter limits; the inserts go in one <c>SaveChanges</c>, batched by EF.
    /// </summary>
    internal sealed class AuditBatchStore : IAuditBatchStore
    {
        private const int IdChunkSize = 1000;
        private readonly AuditDbContext _context;

        public AuditBatchStore(AuditDbContext context)
        {
            _context = context;
        }

        public Task<IReadOnlyCollection<Guid>> GetExistingAccessLogIdsAsync(IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken)
            => ExistingAsync(_context.AccessLogs.AsNoTracking().Select(x => x.Id), ids, cancellationToken);

        public Task<IReadOnlyCollection<Guid>> GetExistingDataAuditIdsAsync(IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken)
            => ExistingAsync(_context.AuditEntries.AsNoTracking().Select(x => x.Id), ids, cancellationToken);

        public void Add(AccessLog accessLog) => _context.AccessLogs.Add(accessLog);

        public void Add(DataAudit dataAudit) => _context.AuditEntries.Add(dataAudit);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);

        public void ClearTracked() => _context.ChangeTracker.Clear();

        private static async Task<IReadOnlyCollection<Guid>> ExistingAsync(IQueryable<Guid> stored,
            IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
        {
            var found = new List<Guid>();
            foreach (var chunk in ids.Distinct().Chunk(IdChunkSize))
            {
                found.AddRange(await stored.Where(id => chunk.Contains(id)).ToListAsync(cancellationToken));
            }
            return found;
        }
    }
}
