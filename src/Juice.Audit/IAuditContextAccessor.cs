namespace Juice.Audit
{
    public interface IAuditContextAccessor : IDisposable
    {
        AuditContext AuditContext { get; }
        void Init(string action, string? user);
        void Init(string action, string? user, string? tenantId)
        {
            Init(action, user);
            AuditContext.SetTenant(tenantId);
        }
    }
}
