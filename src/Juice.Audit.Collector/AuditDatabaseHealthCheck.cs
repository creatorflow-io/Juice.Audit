using Juice.Audit.EF;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Juice.Audit.Collector.Host
{
    /// <summary>Ready when the audit database answers: without it every record comes back retryable.</summary>
    internal sealed class AuditDatabaseHealthCheck : IHealthCheck
    {
        private readonly IServiceScopeFactory _scopes;

        public AuditDatabaseHealthCheck(IServiceScopeFactory scopes) => _scopes = scopes;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
                return await db.Database.CanConnectAsync(cancellationToken)
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("The audit database can't be reached.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("The audit database can't be reached.", ex);
            }
        }
    }
}
