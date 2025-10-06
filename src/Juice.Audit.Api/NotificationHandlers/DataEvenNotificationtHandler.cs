using Juice.Domain.Events;
using Newtonsoft.Json;

namespace Juice.Audit.Api.NotificationHandlers
{
    internal class DataEvenNotificationtHandler<T> : INotificationHandler<T>
        where T : AuditEvent
    {
        private IAuditContextAccessor _auditContextAccessor;

        public DataEvenNotificationtHandler(IAuditContextAccessor auditContextAccessor)
        {
            _auditContextAccessor = auditContextAccessor;
        }

        public ValueTask Handle(T notification, CancellationToken cancellationToken)
        {
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
                    _auditContextAccessor.AuditContext.AccessRecord.TraceId
                    ));
            }
            return ValueTask.CompletedTask;
        }
    }
}
