using System.Diagnostics;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Domain.Events;
using Juice.MultiTenant;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Juice.Audit.Api.NotificationHandlers
{
    /// <summary>
    /// Turns a data change a DbContext reports into an audit entry. With an <see cref="IDataChangeSink"/> (the app
    /// called <c>AddAuditOutbox&lt;TContext&gt;()</c>), the entry goes into the transaction's outbox and commits with
    /// the change. Otherwise, or when the outbox can't take it, it is carried with the scope's access record, held
    /// until its transaction commits.
    /// </summary>
    internal class DataEvenNotificationtHandler<T> : INotificationHandler<T>
        where T : AuditEvent
    {
        private readonly IAuditContextAccessor _auditContextAccessor;
        private readonly ILogger _logger;
        private readonly IDataChangeSink? _sink;
        private readonly ITenantAccessor? _tenantAccessor;

        public DataEvenNotificationtHandler(IAuditContextAccessor auditContextAccessor,
            ILogger<DataEvenNotificationtHandler<T>> logger,
            IDataChangeSink? sink = null,
            ITenantAccessor? tenantAccessor = null)
        {
            _auditContextAccessor = auditContextAccessor;
            _logger = logger;
            _sink = sink;
            _tenantAccessor = tenantAccessor;
        }

        public async ValueTask Handle(T notification, CancellationToken cancellationToken)
        {
            var auditRecord = notification.AuditRecord;
            if (auditRecord == null)
            {
                return;
            }

            if (!_auditContextAccessor.IsInitialized)
            {
                // The outbox doesn't need a scope: the change is saved with the transaction whoever made it
                // (AuditOutboxOptions.RequireScope turns this off). Without the outbox there is no record to attach
                // the change to (work in an app without AddMediatorAudit() or AddMessageAudit()).
                if (_sink != null)
                {
                    var unscoped = CreateEntry(notification, auditRecord, auditRecord.User, CurrentTraceId(),
                        notification.TenantId ?? _tenantAccessor?.Tenant?.Id);
                    if (await _sink.TryAppendAsync(notification.TransactionId, unscoped, cancellationToken))
                    {
                        return;
                    }
                }
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Data change {Name} is not audited: no audit scope is open.", notification.Name);
                }
                return;
            }

            var auditContext = _auditContextAccessor.AuditContext;
            var entry = CreateEntry(notification, auditRecord,
                // A DbContext knows the user only through IHttpContextAccessor; the scope always does.
                auditRecord.User ?? auditContext.AccessRecord.User,
                auditContext.AccessRecord.TraceId,
                notification.TenantId ?? auditContext.TenantId);

            if (_sink != null)
            {
                // The sink takes a change only when it has a transaction, and counts why it didn't.
                if (await _sink.TryAppendAsync(notification.TransactionId, entry, cancellationToken)
                    && notification.TransactionId is { } outboxTransaction)
                {
                    auditContext.AddOutboxedPending(outboxTransaction);
                    return;
                }
                auditContext.MarkBestEffort(entry);
            }

            if (notification.TransactionId is { } transactionId)
            {
                auditContext.AddUncommittedAuditEntries(transactionId, entry);
            }
            else
            {
                auditContext.AddAuditEntries(entry);
            }
        }

        private static DataAudit CreateEntry(T notification, AuditRecord auditRecord, string? user, string? traceId,
            string? tenantId)
            => new(
                user,
                DateTimeOffset.UtcNow,
                notification.Name,
                auditRecord.Database,
                auditRecord.Schema,
                auditRecord.Table,
                JsonConvert.SerializeObject(auditRecord.KeyValues),
                JsonConvert.SerializeObject(new { auditRecord.OriginalValues, auditRecord.CurrentValues }),
                traceId,
                tenantId);

        /// <summary>The W3C trace id of the work, as a scope would record it.</summary>
        private static string? CurrentTraceId()
            => Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.TraceId.ToHexString() : null;
    }
}
