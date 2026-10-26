using System.Runtime.CompilerServices;
using FluentAssertions;
using Juice.Audit.AspNetCore.Middleware;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.MultiTenant;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Tests
{
    public class MultiTenantAuditTests
    {
        [Fact(DisplayName = "Access log should capture the current tenant")]
        public async Task Access_log_should_capture_current_tenantAsync()
        {
            var tenantAccessor = new StubTenantAccessor { Tenant = new StubTenant("tenant-a") };

            var accessLog = await InvokeMiddlewareAsync(tenantAccessor, _ => { });

            accessLog.Should().NotBeNull();
            accessLog!.TenantId.Should().Be("tenant-a");
        }

        [Fact(DisplayName = "Access log should capture a tenant resolved later in the pipeline")]
        public async Task Access_log_should_capture_late_resolved_tenantAsync()
        {
            var tenantAccessor = new StubTenantAccessor();

            var accessLog = await InvokeMiddlewareAsync(tenantAccessor,
                _ => tenantAccessor.Tenant = new StubTenant("tenant-b"));

            accessLog!.TenantId.Should().Be("tenant-b");
        }

        [Fact(DisplayName = "Access log should have no tenant when the app is not multi-tenant")]
        public async Task Access_log_should_have_no_tenant_without_tenant_accessorAsync()
        {
            var accessLog = await InvokeMiddlewareAsync(null, _ => { });

            accessLog.Should().NotBeNull();
            accessLog!.TenantId.Should().BeNull();
        }

        [Fact(DisplayName = "Tenant should not be overwritten once set")]
        public void Tenant_should_not_be_overwritten()
        {
            var accessLog = new AccessLog("action", "user", "tenant-a");
            accessLog.SetTenant("tenant-b");
            accessLog.SetTenant(null);

            accessLog.TenantId.Should().Be("tenant-a");
        }

        [Fact(DisplayName = "Data audit should store the tenant")]
        public void Data_audit_should_store_tenant()
        {
            new DataAudit("user", DateTimeOffset.UtcNow, "Inserted", "db", "dbo", "tbl", "{}", "{}", "trace", "tenant-a")
                .TenantId.Should().Be("tenant-a");

            new DataAudit("user", DateTimeOffset.UtcNow, "Inserted", "db", "dbo", "tbl", "{}", "{}", "trace", "")
                .TenantId.Should().BeNull();

            var tooLong = () => new DataAudit("user", DateTimeOffset.UtcNow, "Inserted", "db", "dbo", "tbl", "{}", "{}", "trace",
                new string('x', Juice.MultiTenant.Constants.TenantIdMaxLength + 1));
            tooLong.Should().Throw<Exception>();
        }

        private static async Task<AccessLog?> InvokeMiddlewareAsync(ITenantAccessor? tenantAccessor, Action<HttpContext> handle)
        {
            var auditService = new CapturingAuditService();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuditContextAccessor();
            services.AddSingleton<IAuditService>(auditService);
            if (tenantAccessor != null)
            {
                services.AddSingleton(tenantAccessor);
            }
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Method = "GET";
            context.Request.Path = "/api/test";

            var middleware = new AuditMiddleware(ctx =>
            {
                handle(ctx);
                return Task.CompletedTask;
            }, "test", new AuditFilterOptions());

            await middleware.InvokeAsync(context);

            return auditService.AccessLog;
        }

        private class CapturingAuditService : IAuditService
        {
            public AccessLog? AccessLog { get; private set; }

            public Task<IOperationResult> PersistAuditInformationAsync(AccessLog accessLog, DataAudit[] auditEntries, CancellationToken token)
            {
                AccessLog = accessLog;
                return Task.FromResult<IOperationResult>(OperationResult.Success);
            }
        }

        private class StubTenantAccessor : ITenantAccessor
        {
            public ITenant? Tenant { get; set; }
        }

        private class StubTenant : ITenant
        {
            public StubTenant(string id)
            {
                Id = id;
                Identifier = id;
            }
            public string? Id { get; }
            public string? Name => Id;
            public string? Identifier { get; }
            public string? OwnerUser => null;
            public string? Tier => null;
            public string? Region => null;
            public object? this[string key] => null;
            public T? GetProperty<T>(Func<T>? defaultValue = null, [CallerMemberName] string? name = null) => default;
            public void SetProperty<T>(T? value, [CallerMemberName] string? name = null) { }
        }
    }
}
