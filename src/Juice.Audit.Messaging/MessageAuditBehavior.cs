using Juice.Audit.Api.Scopes;
using Juice.Messaging;
using Juice.Messaging.Integrations;

namespace Juice.Audit.Messaging
{
    /// <summary>
    /// Opens an audit scope around the handlers of each integration event, as <c>AuditMiddleware</c> does around a
    /// request: one access record per message (method <c>MESSAGE</c>, path = event name) with the data its handlers
    /// change, under the message's W3C trace id, the trace of the request that raised it. Mediator requests the
    /// handlers send join the scope.
    /// </summary>
    internal sealed class MessageAuditBehavior : IIntegrationEventPipelineBehavior
    {
        private readonly AuditScopeRunner _runner;

        public MessageAuditBehavior(AuditScopeRunner runner)
        {
            _runner = runner;
        }

        /// <summary>Outermost, so other behaviors' work is in the scope.</summary>
        public int Order => int.MinValue;

        public ValueTask<EventDispatchResult> Handle(IIntegrationEvent evt, EventDispatchContext context,
            IntegrationEventHandlerDelegate next, CancellationToken cancellationToken)
            => _runner.RunAsync(
                // A message has no user of its own: the trace leads to the request that raised it.
                new AuditScopeRequest(context.EventName, AuditScopeMethods.Message, context.EventName, context.TenantId,
                    evt.MessageId.ToString()),
                () => next(evt, context, cancellationToken),
                result => Outcome(result, context),
                auditContext =>
                {
                    var record = auditContext.AccessRecord;
                    record.SetMetadata("MessageId", evt.MessageId.ToString());
                    record.SetMetadata("Source", context.Source);
                    if (MessageContext.IsInitialized)
                    {
                        record.SetMetadata("CorrelationId", MessageContext.Current.CorrelationId);
                    }
                });

        /// <summary>200 when a handler succeeded, otherwise 500 with the first handler exception.</summary>
        private static AuditScopeOutcome Outcome(EventDispatchResult result, EventDispatchContext context)
        {
            if (result == EventDispatchResult.Success)
            {
                return AuditScopeOutcome.Ok;
            }
            var error = context.HandlerErrors.FirstOrDefault();
            return new AuditScopeOutcome(500, $"Handlers {result}" + (error != null ? ": " + error.Message : ""), error);
        }
    }
}
