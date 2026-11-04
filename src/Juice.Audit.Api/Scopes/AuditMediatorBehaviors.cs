using Juice.Audit.Commands;
using Juice.Domain.Events;
using Juice.Extensions;
using Juice.MultiTenant;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Api.Scopes
{
    /// <summary>
    /// Opens an audit scope around a mediator request sent outside an audited request or message (a background job, a
    /// scheduled task), so the data its handler changes is saved under the current trace id. Inside one, the request
    /// joins its scope. Outside the tracing behavior (<see cref="int.MinValue"/>), so the trace is the request's span,
    /// and outside idempotency, so a duplicate that changes nothing saves nothing.
    /// </summary>
    internal sealed class AuditRequestBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    {
        private readonly AuditScopeRunner _runner;
        private readonly IServiceProvider _services;

        public AuditRequestBehavior(AuditScopeRunner runner, IServiceProvider services)
        {
            _runner = runner;
            _services = services;
        }

        public int Order => AuditScopes.Order;

        public ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next,
            CancellationToken cancellationToken)
            => AuditScopes.IsAudited(typeof(TRequest))
                ? _runner.RunAsync(AuditScopes.Request(typeof(TRequest), AuditScopeMethods.Request, _services),
                    () => next.Invoke(request, cancellationToken), AuditScopes.Outcome)
                : next.Invoke(request, cancellationToken);
    }

    /// <inheritdoc cref="AuditRequestBehavior{TRequest, TResponse}"/>
    internal sealed class AuditRequestBehavior<TRequest> : IPipelineBehavior<TRequest>
        where TRequest : IRequest
    {
        private readonly AuditScopeRunner _runner;
        private readonly IServiceProvider _services;

        public AuditRequestBehavior(AuditScopeRunner runner, IServiceProvider services)
        {
            _runner = runner;
            _services = services;
        }

        public int Order => AuditScopes.Order;

        public async ValueTask Handle(TRequest request, RequestHandlerDelegate<TRequest> next,
            CancellationToken cancellationToken)
            => await _runner.RunAsync(AuditScopes.Request(typeof(TRequest), AuditScopeMethods.Request, _services),
                async () =>
                {
                    await next.Invoke(request, cancellationToken);
                    return true;
                },
                _ => AuditScopeOutcome.Ok);
    }

    /// <summary>
    /// Opens an audit scope around a mediator notification published outside an audited request or message (one
    /// delivered by the local channel, for example). Data change notifications are not wrapped: they report the
    /// changes to the scope that is open.
    /// </summary>
    internal sealed class AuditNotificationBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
        where TNotification : INotification
    {
        private readonly AuditScopeRunner _runner;
        private readonly IServiceProvider _services;

        public AuditNotificationBehavior(AuditScopeRunner runner, IServiceProvider services)
        {
            _runner = runner;
            _services = services;
        }

        public int Order => AuditScopes.Order;

        public async ValueTask Handle(TNotification notification, NotificationHandlerDelegate<TNotification> next,
            CancellationToken cancellationToken)
        {
            if (notification is DataEvent)
            {
                await next.Invoke(notification, cancellationToken);
                return;
            }
            await _runner.RunAsync(AuditScopes.Request(typeof(TNotification), AuditScopeMethods.Notification, _services),
                async () =>
                {
                    await next.Invoke(notification, cancellationToken);
                    return true;
                },
                _ => AuditScopeOutcome.Ok);
        }
    }

    internal static class AuditScopes
    {
        /// <summary>Inside the tracing behavior (<see cref="int.MinValue"/>), outside idempotency (+10).</summary>
        public const int Order = int.MinValue + 5;

        /// <summary>Saving an audit record sends this command: it is part of the scope being saved.</summary>
        public static bool IsAudited(Type requestType) => requestType != typeof(SaveAuditInfoCommand);

        public static AuditScopeRequest Request(Type type, string method, IServiceProvider services)
        {
            var name = type.GetGenericTypeName();
            return new AuditScopeRequest(name, method, name, services.GetService<ITenantAccessor>()?.Tenant?.Id);
        }

        /// <summary>A failed <see cref="IOperationResult"/> is 400, or 500 when it carries an exception.</summary>
        public static AuditScopeOutcome Outcome<TResponse>(TResponse response)
            => response is IOperationResult { Succeeded: false } result
                ? new AuditScopeOutcome(result.Exception != null ? 500 : 400, result.Message, result.Exception)
                : AuditScopeOutcome.Ok;
    }
}
