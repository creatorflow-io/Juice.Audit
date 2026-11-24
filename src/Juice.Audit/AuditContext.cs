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

        public bool IsRequestedForAuditLog => AuditEntries.Count > 0 || UncommittedAuditEntryCount > 0
            || OutboxedChangeCount > 0;

        /// <summary>
        /// Changes reported inside a transaction, held until it commits. Those still here when the scope ends were never
        /// committed: they are not saved, but the attempt is still recorded.
        /// </summary>
        private readonly Dictionary<Guid, List<DataAudit>> _uncommittedEntries = new();

        /// <summary>
        /// Changes taken by a transaction's outbox (<see cref="IDataChangeSink"/>), counted until it commits. They travel
        /// with the outbox, not with this record.
        /// </summary>
        private readonly Dictionary<Guid, int> _outboxedPending = new();

        /// <summary>Changes carried with this record because the outbox couldn't take them.</summary>
        private readonly HashSet<DataAudit> _bestEffort = new(ReferenceEqualityComparer.Instance);

        /// <summary>Changes of transactions that never committed, held here or in their outbox.</summary>
        public int UncommittedAuditEntryCount => _uncommittedEntries.Values.Sum(entries => entries.Count)
            + _outboxedPending.Values.Sum();

        /// <summary>Committed changes sent through the outbox; find them by trace id.</summary>
        public int OutboxedChangeCount { get; private set; }

        /// <summary>The changes in <see cref="AuditEntries"/> that the outbox couldn't take.</summary>
        public int BestEffortChangeCount => AuditEntries.Count(_bestEffort.Contains);

        public Version? Version => Assembly.GetEntryAssembly()?.GetName().Version;
        public AccessLog AccessRecord { get; private set; }
        public List<DataAudit> AuditEntries { get; private set; } = new();

        public string? TenantId => AccessRecord.TenantId;

        public AuditContext(string action, string? user)
        {
            AccessRecord = new AccessLog(action, user);
        }

        public AuditContext(string action, string? user, string? tenantId)
        {
            AccessRecord = new AccessLog(action, user, tenantId);
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

        /// <summary>Holds changes made inside a transaction until <see cref="CommitAuditEntries"/>.</summary>
        public void AddUncommittedAuditEntries(Guid transactionId, params DataAudit[] auditEntries)
        {
            if (!_uncommittedEntries.TryGetValue(transactionId, out var entries))
            {
                _uncommittedEntries[transactionId] = entries = new();
            }
            entries.AddRange(auditEntries);
        }

        /// <summary>A change of <paramref name="transactionId"/> went into its outbox.</summary>
        public void AddOutboxedPending(Guid transactionId)
            => _outboxedPending[transactionId] = _outboxedPending.GetValueOrDefault(transactionId) + 1;

        /// <summary>The outbox couldn't take <paramref name="entry"/>: it is carried with this record.</summary>
        public void MarkBestEffort(DataAudit entry)
            => _bestEffort.Add(entry);

        /// <summary>
        /// The transaction committed: its held changes join <see cref="AuditEntries"/>, and its outboxed changes are
        /// counted as committed.
        /// </summary>
        public void CommitAuditEntries(Guid transactionId)
        {
            if (_uncommittedEntries.Remove(transactionId, out var entries))
            {
                AuditEntries.AddRange(entries);
            }
            if (_outboxedPending.Remove(transactionId, out var outboxed))
            {
                OutboxedChangeCount += outboxed;
            }
        }

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

        public void SetTenant(string? tenantId)
            => AccessRecord.SetTenant(tenantId);

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
                    _uncommittedEntries.Clear();
                    _outboxedPending.Clear();
                    _bestEffort.Clear();
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
