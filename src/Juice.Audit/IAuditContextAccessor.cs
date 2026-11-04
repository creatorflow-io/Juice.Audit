namespace Juice.Audit
{
    public interface IAuditContextAccessor : IDisposable
    {
        AuditContext AuditContext { get; }

        /// <summary>
        /// <c>true</c> while an audit context is open in this scope: inside a request audited by <c>UseAudit()</c>, or
        /// a mediator request or message audited by <c>AddMediatorAudit()</c> or <c>AddMessageAudit()</c>.
        /// </summary>
        bool IsInitialized
        {
            get
            {
                try
                {
                    _ = AuditContext;
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
        void Init(string action, string? user);
        void Init(string action, string? user, string? tenantId)
        {
            Init(action, user);
            AuditContext.SetTenant(tenantId);
        }

        /// <summary>
        /// Closes the audit context once it is saved, so the next unit of work in the same DI scope (a background job
        /// sending two commands, for example) opens its own.
        /// </summary>
        void Reset() => throw new NotSupportedException($"{GetType().Name} cannot close its audit context.");
    }
}
