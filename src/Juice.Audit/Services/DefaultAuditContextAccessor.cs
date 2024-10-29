namespace Juice.Audit.Services
{
    internal class DefaultAuditContextAccessor : IAuditContextAccessor
    {
        public AuditContext AuditContext => _auditContext ?? throw new InvalidOperationException("AuditContext is not initialized. Please add the AuditMiddleware to app pipeline.");

        private AuditContext? _auditContext;

        public void Init(string action, string? user)
        {
            _auditContext = new AuditContext(action, user);
        }


        #region IDisposable Support

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _auditContext?.Dispose();
                    _auditContext = null;
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
