using Juice.Audit.Api.Scopes;
using Juice.Audit.Messaging;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class MessageAuditServiceCollectionExtensions
    {
        /// <summary>
        /// Audits integration event handlers as <c>UseAudit()</c> audits requests: each event's handlers run in an
        /// audit scope, saved by the same rules (see <see cref="AuditScopeOptions"/>) as an access record for the
        /// message (method <c>MESSAGE</c>, path = event name) with the data its handlers change, under the message's
        /// trace id, the trace of the request that raised the event.
        /// <para>Use with <c>ConfigureAuditDefault()</c> or <c>ConfigureAuditGrpcClient()</c>, which register the audit
        /// context, the data event handler and the audit service, and with <c>AddMediatorAudit()</c> for work outside
        /// messages.</para>
        /// </summary>
        public static IServiceCollection AddMessageAudit(this IServiceCollection services,
            Action<AuditScopeOptions>? configure = default)
        {
            services.AddAuditScopeRunner(configure);
            services.AddIntegrationEventPipelineBehavior<MessageAuditBehavior>();
            return services;
        }
    }
}
