using System.Linq.Expressions;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Domain;

namespace Juice.Audit.Tests.Host.Mockservices
{
    internal class DataAuditRepository : IDataAuditRepository
    {
        private readonly ILogger<DataAuditRepository> _logger;
        public DataAuditRepository(ILogger<DataAuditRepository> logger)
        {
            _logger = logger;
        }

        public IUnitOfWork<DataAudit> UnitOfWork => throw new NotImplementedException();

        public Task<IOperationResult<DataAudit>> AddAsync(DataAudit entity, CancellationToken token)
        {
            _logger.LogInformation("AuditEntry was added!");
            return Task.FromResult(OperationResult.Result(entity));
        }
        public Task AddRangeAsync(IEnumerable<DataAudit> auditEntries, CancellationToken token)
        {
            _logger.LogInformation("AuditEntries were added! {0}", auditEntries.Count());
            return Task.CompletedTask;
        }
        public Task<IOperationResult> DeleteAsync(DataAudit entity, CancellationToken token) => throw new NotImplementedException();
        public Task<bool> ExistsAsync<TKey>(TKey id, CancellationToken token = default) => throw new NotImplementedException();
        public Task<DataAudit?> FindAsync(Expression<Func<DataAudit, bool>> predicate, CancellationToken token) => throw new NotImplementedException();
        public Task<DataAudit?> FindAsync(Expression<Func<DataAudit, bool>> predicate, bool readOnly = false, CancellationToken token = default) => throw new NotImplementedException();
        public Task<DataAudit?> GetAsync<TKey>(TKey id, CancellationToken token = default) => throw new NotImplementedException();
        public IQueryable<DataAudit> Query() => throw new NotImplementedException();
        public Task<DataAudit?> ReadAsync<TKey>(TKey id, CancellationToken token = default) => throw new NotImplementedException();
        public Task<IOperationResult> UpdateAsync(DataAudit entity, CancellationToken token) => throw new NotImplementedException();
    }
}
