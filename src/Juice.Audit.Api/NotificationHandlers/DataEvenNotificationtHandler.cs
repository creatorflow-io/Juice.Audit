using Juice.Domain.Events;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Juice.Audit.Api.NotificationHandlers
{
    internal class DataEvenNotificationtHandler<T> : INotificationHandler<T>
        where T : AuditEvent
    {
        private IAuditContextAccessor _auditContextAccessor;
        private ILogger _logger;

        public DataEvenNotificationtHandler(IAuditContextAccessor auditContextAccessor,
            ILogger<DataEvenNotificationtHandler<T>> logger)
        {
            _auditContextAccessor = auditContextAccessor;
            _logger = logger;
        }

        public ValueTask Handle(T notification, CancellationToken cancellationToken)
        {
            // Outside an audited request, mediator request or message (work in an app without AddMediatorAudit() or
            // AddMessageAudit()) there is no record to attach the change to.
            if (!_auditContextAccessor.IsInitialized)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Data change {Name} is not audited: no audit scope is open.", notification.Name);
                }
                return ValueTask.CompletedTask;
            }
            var auditRecord = notification.AuditRecord;
            if (auditRecord != null)
            {
                _auditContextAccessor.AuditContext.AddAuditEntries(new Domain.DataAuditAggregate.DataAudit(
                    auditRecord.User,
                    DateTimeOffset.UtcNow,
                    notification.Name,
                    auditRecord.Database,
                    auditRecord.Schema,
                    auditRecord.Table,
                    JsonConvert.SerializeObject(auditRecord.KeyValues),
                    JsonConvert.SerializeObject(new { auditRecord.OriginalValues, auditRecord.CurrentValues }),
                    _auditContextAccessor.AuditContext.AccessRecord.TraceId,
                    notification.TenantId ?? _auditContextAccessor.AuditContext.TenantId
                    ));
            }
            return ValueTask.CompletedTask;
        }
    }
}
