using Juice.Domain;

namespace Juice.Audit.Domain.DataAuditAggregate
{
    public class DataAudit : AggregateRoot<INotification>
    {
        public DataAudit() { }
        public DataAudit(string? user, DateTimeOffset dateTime, string action, string? database, string? schema, string table, string keyValues, string dataChanges, string? traceId, string? tenantId = default)
        {
            User = user;
            DateTime = dateTime;
            Action = action;
            Db = database;
            Schema = schema;
            Tbl = table;
            Kvps = keyValues;
            Changes = dataChanges;
            TraceId = traceId;
            TenantId = string.IsNullOrEmpty(tenantId) ? null : tenantId;

            this.NotNullOrWhiteSpace(Action, LengthConstants.NameLength);

            this.NotExceededLength(TraceId, LengthConstants.IdentityLength);
            this.NotExceededLength(TenantId, Juice.MultiTenant.Constants.TenantIdMaxLength);
            this.NotExceededLength(User, LengthConstants.NameLength);
            this.NotExceededLength(Db, LengthConstants.NameLength);
            this.NotExceededLength(Schema, LengthConstants.NameLength);
            this.NotExceededLength(Tbl, LengthConstants.NameLength);
            this.NotExceededLength(Kvps, LengthConstants.NameLength);

            this.ValidateJson(Changes);
            this.ValidateJson(Kvps);

            this.ThrowIfHasErrors();
        }

        public Guid Id { get; set; }
        public string? User { get; private set; }
        public DateTimeOffset DateTime { get; private set; }
        public string Action { get; private set; }
        public string? Db { get; private set; }
        public string? Schema { get; private set; }
        public string Tbl { get; private set; }
        public string Kvps { get; private set; }
        public string Changes { get; private set; }
        public string? TraceId { get; private set; }
        /// <summary>
        /// The tenant that owns the audited data. <c>null</c> for the root tenant or a non multi-tenant app.
        /// </summary>
        public string? TenantId { get; private set; }

        public void SetTraceId(string requestId)
            => TraceId = requestId;
    }
}
