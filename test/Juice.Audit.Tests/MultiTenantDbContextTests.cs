using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.EF;
using Juice.EF.Extensions;
using Juice.XUnit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Tests
{
    /// <summary>
    /// Requires <c>ConnectionStrings:SqlServerConnection</c> / <c>ConnectionStrings:PostgreConnection</c>
    /// (environment variables <c>ConnectionStrings__SqlServerConnection</c> / <c>ConnectionStrings__PostgreConnection</c>).
    /// </summary>
    public class MultiTenantDbContextTests
    {
        [IgnoreOnCITheory(DisplayName = "Audit records should be isolated per tenant")]
        [InlineData("SqlServer")]
        [InlineData("PostgreSQL")]
        public async Task Audit_records_should_be_isolated_per_tenantAsync(string provider)
        {
            var tenantAccessor = new StubMultiTenantContextAccessor();
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IMultiTenantContextAccessor>(tenantAccessor);
            services.AddAuditDbContext(configuration, options => options.DatabaseProvider = provider);
            using var sp = services.BuildServiceProvider();

            using (var scope = sp.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<AuditDbContext>().MigrateAsync();
            }

            var traceId = Guid.NewGuid().ToString();

            // Tenant A: TenantId not set on the record, stamped from the current tenant on save
            tenantAccessor.SetTenant("tenant-a");
            await AddAccessLogAsync(sp, traceId, tenantId: null);

            // Root (e.g. gRPC audit server): writes a record for another tenant
            tenantAccessor.SetTenant(null);
            await AddAccessLogAsync(sp, traceId, tenantId: "tenant-b");

            tenantAccessor.SetTenant("tenant-a");
            var tenantRecords = await QueryAsync(sp, traceId);
            tenantRecords.Select(r => r.TenantId).Should().BeEquivalentTo(new[] { "tenant-a" });

            tenantAccessor.SetTenant(null);
            var rootRecords = await QueryAsync(sp, traceId);
            rootRecords.Select(r => r.TenantId).Should().BeEquivalentTo(new[] { "tenant-a", "tenant-b" });
        }

        private static async Task AddAccessLogAsync(IServiceProvider sp, string traceId, string? tenantId)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var accessLog = new AccessLog("tenant_test", "xunit", tenantId);
            accessLog.SetRequestInfo(new RequestInfo("GET", "/tenant/test", null, null, null, "https", null, traceId, "localhost"));
            db.AccessLogs.Add(accessLog);
            await db.SaveChangesAsync();
        }

        private static async Task<List<AccessLog>> QueryAsync(IServiceProvider sp, string traceId)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            return await db.AccessLogs.AsNoTracking()
                .Where(l => l.Request!.TraceId == traceId)
                .ToListAsync();
        }

        private class StubMultiTenantContextAccessor : IMultiTenantContextAccessor
        {
            public IMultiTenantContext MultiTenantContext { get; private set; } = new MultiTenantContext<TenantInfo>();

            public void SetTenant(string? tenantId)
            {
                MultiTenantContext = new MultiTenantContext<TenantInfo>
                {
                    TenantInfo = tenantId == null ? null : new TenantInfo { Id = tenantId, Identifier = tenantId, Name = tenantId }
                };
            }
        }
    }
}
