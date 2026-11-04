using System.Diagnostics;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Measurement;
using Juice.Measurement.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Audit.Api.Scopes
{
    /// <summary>What a scope opened outside an HTTP request records as its request.</summary>
    /// <param name="Action">The access record's action.</param>
    /// <param name="Method">One of <see cref="AuditScopeMethods"/>.</param>
    /// <param name="Path">The request's, notification's or event's name.</param>
    /// <param name="TenantId">The tenant the work runs for.</param>
    /// <param name="FallbackTraceId">The trace id when there is no W3C activity.</param>
    public sealed record AuditScopeRequest(string Action, string Method, string Path, string? TenantId,
        string? FallbackTraceId = null);

    /// <summary>How the work ended, as an HTTP-like status so access log filters work: 200, 400 or 500.</summary>
    public readonly record struct AuditScopeOutcome(int Status, string? Message = null, Exception? Error = null)
    {
        public static AuditScopeOutcome Ok => new(200);
    }

    /// <summary>
    /// Opens an audit scope around a unit of work outside an HTTP request, as <c>AuditMiddleware</c> does around a
    /// request: the data it changes is saved with an access record under the current W3C trace id. When a scope is
    /// already open (the request's, or an outer unit of work's), the work joins it.
    /// </summary>
    public sealed class AuditScopeRunner
    {
        private readonly IAuditContextAccessor _accessor;
        private readonly IServiceProvider _services;
        private readonly AuditScopeOptions _options;
        private readonly ILogger _logger;

        public AuditScopeRunner(IAuditContextAccessor accessor, IServiceProvider services,
            IOptions<AuditScopeOptions> options, ILogger<AuditScopeRunner> logger)
        {
            _accessor = accessor;
            _services = services;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Runs <paramref name="next"/> in a new audit scope, saved and closed when it ends, or in the open one.
        /// </summary>
        /// <param name="outcome">How the work ended, from its result; an exception is recorded as 500.</param>
        /// <param name="enrich">Adds metadata to the new scope's access record.</param>
        public async ValueTask<T> RunAsync<T>(AuditScopeRequest request, Func<ValueTask<T>> next,
            Func<T, AuditScopeOutcome> outcome, Action<AuditContext>? enrich = default)
        {
            if (_accessor.IsInitialized)
            {
                return await next();
            }

            // The work's span: a message's continues the trace of the request that raised it.
            var activity = Activity.Current;
            var traceId = AuditRecorder.TraceId(activity, request.FallbackTraceId ?? Guid.NewGuid().ToString("N"));
            if (!TryOpen(request, activity, traceId, enrich))
            {
                return await next();
            }

            // The scope's tracker is shared with the mediator: dispose only one made here.
            var tracker = _services.GetService<ITimeTracker>();
            var ownedTracker = tracker == null;
            tracker ??= new TimeTracker();
            var ended = new AuditScopeOutcome(500);
            try
            {
                T result;
                using (tracker.BeginScope("Invoke"))
                {
                    result = await next();
                }
                ended = outcome(result);
                return result;
            }
            catch (Exception ex)
            {
                ended = new AuditScopeOutcome(500, ex.Message, ex);
                throw;
            }
            finally
            {
                await CloseAsync(request, ended, tracker, activity, traceId);
                if (ownedTracker)
                {
                    tracker.Dispose();
                }
            }
        }

        private bool TryOpen(AuditScopeRequest request, Activity? activity, string traceId, Action<AuditContext>? enrich)
        {
            try
            {
                _accessor.Init(request.Action, null, request.TenantId);
                var auditContext = _accessor.AuditContext;
                auditContext.SetRequestInfo(new RequestInfo(
                    request.Method,
                    Validatable.TrimExceededLength(request.Path, LengthConstants.NameLength) ?? "",
                    default, default, default, default, default,
                    traceId,
                    default));
                auditContext.SetServerInfo(AuditRecorder.CreateServerInfo(_options.AppName));
                AuditRecorder.SetSpanId(auditContext, activity);
                enrich?.Invoke(auditContext);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while initializing the audit context of {Action}", request.Action);
                Reset();
                return false;
            }
        }

        private async Task CloseAsync(AuditScopeRequest request, AuditScopeOutcome outcome, ITimeTracker tracker,
            Activity? activity, string traceId)
        {
            try
            {
                var auditContext = _accessor.AuditContext;
                var elapsed = tracker.ElapsedTime;
                var isMeasureRequested = auditContext.IsRequestedForMeasureLog(elapsed);
                var isTimeExceeded = _options.ExecutionTimeThreshold.HasValue
                    && elapsed.TotalMilliseconds > _options.ExecutionTimeThreshold;
                var isRequested = auditContext.IsRequestedForAccessLog(outcome.Status);
                string? rule = null;
                var isFilterMatched = _options.Filter != null
                    && _options.Filter.IsMatch(request.Path, request.Method, outcome.Status, out rule, out _, out _);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("{Method} {Path}. Status: {Status}; AccessLog requested: {AccessLog}; DataAudit requested: {DataAudit}; TimeExceeded: {TimeExceeded}; MeasureRequested {Measure}; FilterMatched {Filter} {Rule}",
                        request.Method, request.Path, outcome.Status, isRequested, auditContext.IsRequestedForAuditLog,
                        isTimeExceeded, isMeasureRequested, isFilterMatched, rule);
                }

                if (isRequested || auditContext.IsRequestedForAuditLog || isTimeExceeded || isMeasureRequested
                    || isFilterMatched)
                {
                    AuditRecorder.SetReasons(auditContext, isFilterMatched, rule, isRequested, isMeasureRequested,
                        isTimeExceeded);
                    auditContext.UpdateResponseInfo(response =>
                    {
                        if (!string.IsNullOrEmpty(outcome.Message))
                        {
                            response.TrySetMessage(outcome.Message);
                        }
                        if (outcome.Error != null)
                        {
                            response.TrySetError(outcome.Error.StackTrace ?? outcome.Error.ToString());
                        }
                        response.SetResponseInfo(outcome.Status, "", (long)elapsed.TotalMilliseconds);
                    });
                    await AuditRecorder.PersistAsync(_services, auditContext, tracker, _logger);
                }

                if (isTimeExceeded || isMeasureRequested)
                {
                    await AuditRecorder.SaveTimeAsync(_services, auditContext, tracker, activity, traceId, _logger);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while saving the audit information of {Action}, trace {TraceId}",
                    request.Action, traceId);
            }
            finally
            {
                Reset();
            }
        }

        private void Reset()
        {
            try
            {
                _accessor.Reset();
            }
            catch (NotSupportedException ex)
            {
                _logger.LogWarning(ex, "The audit context stays open after its unit of work.");
            }
        }
    }
}
