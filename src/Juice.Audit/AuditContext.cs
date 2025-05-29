using System.Net;
using System.Reflection;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Newtonsoft.Json;

namespace Juice.Audit
{
    public class AuditContext : IDisposable
    {
        public bool IsRequestedForMeasureLog(TimeSpan elapsed)
            => _requestMeasureLog && (!_requestMeasureLogThreshold.HasValue || elapsed >= _requestMeasureLogThreshold);
        private bool _requestMeasureLog;
        private TimeSpan? _requestMeasureLogThreshold;

        public bool IsRequestedForAccessLog(int statusCode)
            => _requestAccessLog && (_requestAccessLogHttpStatusCodes.Length == 0 || _requestAccessLogHttpStatusCodes.Contains(statusCode));
        private bool _requestAccessLog;
        private int[] _requestAccessLogHttpStatusCodes = Array.Empty<int>();

        public bool IsRequestedForAuditLog => AuditEntries.Count > 0;
        public Version? Version => Assembly.GetEntryAssembly()?.GetName().Version;
        public AccessLog AccessRecord { get; private set; }
        public List<DataAudit> AuditEntries { get; private set; } = new();

        public AuditContext(string action, string? user)
        {
            AccessRecord = new AccessLog(action, user);
        }

        /// <summary>
        /// Shared data between different parts of the application
        /// </summary>
        public Dictionary<string, object?> Items { get; private set; } = new();

        public void SetAction(string action)
            => AccessRecord.SetAction(action);

        public void SetRequestInfo(RequestInfo requestInfo)
            => AccessRecord.SetRequestInfo(requestInfo);

        public void SetServerInfo(ServerInfo serverInfo)
            => AccessRecord.SetServerInfo(serverInfo);

        public void UpdateResponseInfo(Action<ResponseInfo> update)
            => AccessRecord.UpdateResponseInfo(update);

        public void AddAuditEntries(params DataAudit[] auditEntries)
            => AuditEntries.AddRange(auditEntries);

        public void RequestAccessLog(params int[] statusCodes)
        {
            _requestAccessLog = true;
            _requestAccessLogHttpStatusCodes = statusCodes;
        }

        public void RequestMeasureLog(TimeSpan? threshold)
        {
            _requestMeasureLogThreshold = threshold;
            _requestMeasureLog = true;
        }

        public void SetUser(string? user)
            => AccessRecord.SetUser(user);

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
        #region IDisposable Support

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // dispose managed state (managed objects).
                    AuditEntries.Clear();
                    Items.Clear();
                    _requestAccessLogHttpStatusCodes = Array.Empty<int>();
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
