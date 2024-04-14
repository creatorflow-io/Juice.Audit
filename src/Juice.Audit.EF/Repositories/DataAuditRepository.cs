using Juice.Audit.Domain.DataAuditAggregate;
using Juice.EF;

namespace Juice.Audit.EF.Repositories
{
    internal class DataAuditRepository : RepositoryBase<DataAudit, AuditDbContext>,
        IDataAuditRepository
    {
        private readonly AuditDbContext _context;
        public DataAuditRepository(AuditDbContext context) : base(context)
        {
            _context = context;
        }

        public Task AddRangeAsync(IEnumerable<DataAudit> auditEntries, CancellationToken token)
        {
            _context.AuditEntries.AddRange(auditEntries);
            return _context.SaveChangesAsync(token);
        }
    }
}
