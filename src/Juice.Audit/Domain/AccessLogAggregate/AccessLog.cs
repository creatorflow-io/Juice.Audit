using System.ComponentModel.DataAnnotations.Schema;
using Juice.Domain;
using Juice.MediatR;
using Newtonsoft.Json.Linq;

namespace Juice.Audit.Domain.AccessLogAggregate
{
    public class AccessLog : AggregateRoot<INotification>
    {
        public AccessLog() { }
        public AccessLog(string action, string? user)
        {
            Action = action;
            User = user;
            DateTime = DateTimeOffset.UtcNow;
            Response = new ResponseInfo();
        }
        public AccessLog(string action, string? user, string? tenantId)
            : this(action, user)
        {
            SetTenant(tenantId);
        }
        public Guid Id { get; set; }
        public DateTimeOffset DateTime { get; init; }

        public string? User { get; private set; }

        /// <summary>
        /// The tenant the request belongs to. <c>null</c> for the root tenant or a non multi-tenant app.
        /// </summary>
        public string? TenantId { get; private set; }

        public string Action { get; private set; }

        public bool IsRestricted { get; private set; }

        [NotMapped]
        public string? TraceId => Request?.TraceId;

        public JObject Metadata { get; private set; } = new();

        public RequestInfo? Request { get; private set; }
        public ServerInfo? Server { get; private set; }
        public ResponseInfo? Response { get; private set; }

        public void SetRequestInfo(RequestInfo requestInfo)
        {
            Request = requestInfo;
        }

        public void SetAccessZone(string zone)
        {
            Request?.SetAccessZone(zone);
        }

        public void SetServerInfo(ServerInfo serverInfo)
            => Server = serverInfo;

        public void UpdateResponseInfo(Action<ResponseInfo> update)
        {
            if (Response is null)
            {
                Response = new ResponseInfo();
            }
            update.Invoke(Response);
        }

        public void SetMetadata(string key, string value)
        {
            Metadata[key] = value;
        }

        public void SetMetadataJson(string json)
        {
            Metadata = JObject.Parse(json);
        }

        public void SetAction(string action)
            => Action = action;

        public void SetUser(string? user)
            => User ??= user;

        /// <summary>
        /// Set the tenant once, like <see cref="SetUser"/>: a tenant that is already set is never overwritten.
        /// </summary>
        public void SetTenant(string? tenantId)
        {
            if (TenantId != null || string.IsNullOrEmpty(tenantId))
            {
                return;
            }
            TenantId = tenantId;
            this.NotExceededLength(TenantId, Juice.MultiTenant.Constants.TenantIdMaxLength);
            this.ThrowIfHasErrors();
        }

        public void Restricted()
        {
            IsRestricted = true;
        }
    }
}
