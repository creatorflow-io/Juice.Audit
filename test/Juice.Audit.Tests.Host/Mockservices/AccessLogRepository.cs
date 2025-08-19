using System.Linq.Expressions;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Domain;

namespace Juice.Audit.Tests.Host.Mockservices
{
    internal class AccessLogRepository : IAccessLogRepository
    {
        private readonly ILogger<AccessLogRepository> _logger;
        public AccessLogRepository(ILogger<AccessLogRepository> logger)
        {
            _logger = logger;
        }

        public IUnitOfWork<AccessLog> UnitOfWork => throw new NotImplementedException();

        public Task<IOperationResult<AccessLog>> AddAsync(AccessLog entity, CancellationToken token)
        {
            _logger.LogInformation("AccessRecord was added!");
            return Task.FromResult(OperationResult.Result(entity));
        }
        public Task<IOperationResult> DeleteAsync(AccessLog entity, CancellationToken token) => throw new NotImplementedException();
        public Task<bool> ExistsAsync<TKey>(TKey id, CancellationToken token = default) => throw new NotImplementedException();
        public Task<AccessLog?> FindAsync(Expression<Func<AccessLog, bool>> predicate, CancellationToken token) => throw new NotImplementedException();
        public Task<AccessLog?> FindAsync(Expression<Func<AccessLog, bool>> predicate, bool readOnly = false, CancellationToken token = default) => throw new NotImplementedException();
        public Task<AccessLog?> GetAsync<TKey>(TKey id, CancellationToken token = default) => throw new NotImplementedException();
        public IQueryable<AccessLog> Query() => throw new NotImplementedException();
        public Task<AccessLog?> ReadAsync<TKey>(TKey id, CancellationToken token = default) => throw new NotImplementedException();
        public Task<IOperationResult> UpdateAsync(AccessLog entity, CancellationToken token) => throw new NotImplementedException();
    }
}
