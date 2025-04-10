using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using Juice.Audit.Api.Extensions;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Measurement;
using Juice.Measurement.Internal;
using Juice.Measurement.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Juice.Audit.AspNetCore.Middleware
{
    public class AuditMiddleware
    {
        private RequestDelegate _next;
        private string _appName;
        private AuditFilterOptions _filter;
        private string? _action;
        private IDictionary<string, string>? _routeValues;

        public AuditMiddleware(RequestDelegate next, string appName, AuditFilterOptions options, string action, IDictionary<string, string> routeValues)
        {
            _next = next;
            _appName = appName;
            _filter = options;
            _routeValues = routeValues;
            _action = action;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            using var auditContextAccessor = context.RequestServices.GetRequiredService<IAuditContextAccessor>();
            var logger = context.RequestServices.GetRequiredService<ILogger<AuditMiddleware>>();

            using var tracker = context.RequestServices.GetService<ITimeTracker>() ?? new TimeTracker();

            try
            {
                InitAuditContext(auditContextAccessor, context);
                tracker.Checkpoint("InitAuditContext");
                if(logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Version: {0}", auditContextAccessor.AuditContext.Version);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error while initializing the audit context");
            }

            try
            {
                PreRequestCollectInfo(auditContextAccessor, tracker, context);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error while collecting the request information");
            }

            bool isMatch = false;
            bool isTimeExceeded = false;
            try
            {
                using (tracker.BeginScope("Invoke"))
                {
                    await _next(context);
                }


                var status = context.RequestAborted.IsCancellationRequested
                    ? _filter.RequestAbortedStatusCode
                    : context.Response.StatusCode;
                isTimeExceeded =
                    auditContextAccessor.AuditContext.IsRequestedForMeasureLog(tracker.ElapsedTime)
                    || _filter.ExecutionTimeThreshold.HasValue && tracker.ElapsedTime.TotalMilliseconds > _filter.ExecutionTimeThreshold;

                isMatch = auditContextAccessor.AuditContext.IsRequestedForAccessLog(status)
                    || auditContextAccessor.AuditContext.IsRequestedForAuditLog
                    || isTimeExceeded
                    || _filter.IsMatch(context.Request.Path, context.Request.Method, status, out _, out _action, out _routeValues);
                
                if (!isMatch && !isTimeExceeded)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug("AuditMiddleware.InvokeAsync: Skip CollectResponseInfo because the conditions do not match. Status: {0}; AccessLog requested: {1}; DataAudit requested: {2}; TimeExceeded: {3}",
                            status, auditContextAccessor.AuditContext.IsRequestedForAccessLog(status), auditContextAccessor.AuditContext.IsRequestedForAuditLog, isTimeExceeded);
                    }
                    return;
                }

                try
                {
                    PostRequestColllectInfo(auditContextAccessor, context, tracker);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error while collecting the response information");
                }
            }
            catch (Exception ex)
            {
                isMatch = true;
                try
                {
                    PostRequestColllectInfo(auditContextAccessor, context, tracker, ex);
                }
                catch (Exception ex1)
                {
                    logger.LogWarning(ex1, "Error while collecting the response error");
                }
                throw;
            }
            finally
            {
                if (isMatch)
                {
                    try
                    {
                        using var _ = tracker.BeginScope("SaveAuditData");
                        var auditService = context.RequestServices.GetService<IAuditService>();
                        tracker.Checkpoint("GetAuditService");

                        if (auditService != null)
                        {
                            var rs = await auditService.PersistAuditInformationAsync(auditContextAccessor.AuditContext.AccessRecord,
                                [.. auditContextAccessor.AuditContext.AuditEntries], default);
                            if (!rs.Succeeded)
                            {
                                logger.LogWarning("Error while saving audit information. {0}", rs.ToString());
                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.LogTrace(rs.StackTrace);
                                }
                            }
                            tracker.Checkpoint("Persist");
                        }
                        else if (logger.IsEnabled(LogLevel.Debug))
                        {
                            logger.LogDebug("AuditMiddleware.InvokeAsync: Skip PersistAuditInformation because IAuditService is not registered.");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, $"Error while saving audit information");
                    }
                }

                if (isTimeExceeded)
                {
                    try
                    {
                        using var _ = tracker.BeginScope("SaveTrackData");
                        var timeRepository = context.RequestServices.GetService<ITimeRepository>();
                        _.Dispose();
                        if (timeRepository != null)
                        {
                            await timeRepository.SaveTrackDataAsync(tracker, context.TraceIdentifier,
                                auditContextAccessor.AuditContext.AccessRecord.Action,
                                auditContextAccessor.AuditContext.AccessRecord.Action);
                        }
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.LogTrace(tracker.ToString(true));
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error while saving execution time trace data");
                    }
                }
            }

        }

        private string? GetUser(HttpContext context)
        {
            return context.User.FindFirst("preferred_username")?.Value
                ?? context.User.FindFirst("name")?.Value
                ?? context.User.FindFirst(ClaimTypes.Name)?.Value;
        }

        private void InitAuditContext(IAuditContextAccessor auditContextAccessor,
            HttpContext context)
        {
            var (path, id) = context.Request.Path.GetPathComponents();

            var action = _action ?? StringUtils.PathToAction(path) ?? "Unknown";

            auditContextAccessor.Init(action, GetUser(context));
            context.Response.Headers.TryAdd("X-Trace-Id", context.TraceIdentifier);
        }

        private void CollectRequestInfo(IAuditContextAccessor auditContextAccessor,
            HttpContext context)
        {

            var requestInfo = new RequestInfo(
                context.Request.Method,
                context.Request.Path,
                default,
                context.Request.QueryString.HasValue ? context.Request.QueryString.Value : default,
                JsonConvert.SerializeObject(context.Request.Headers
                    .Where(h => _filter.IsReqHeaderMatch(h.Key))
                    .ToDictionary(x => x.Key, x => x.Value)),
                context.Request.Scheme,
                context.Connection.RemoteIpAddress?.ToString(),
                context.TraceIdentifier,
                context.Request.Host.ToString()
                )
            ;
            if (_routeValues != null)
            {
                requestInfo.SetData(_routeValues.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value));
            }
            auditContextAccessor.AuditContext.SetRequestInfo(requestInfo);
        }

        private void CollectServerInfo(IAuditContextAccessor auditContextAccessor)
        {
            var assembly = Assembly.GetEntryAssembly();
            FileVersionInfo? fvi = assembly != null ? FileVersionInfo.GetVersionInfo(assembly.Location) : default;
            var serverInfo = new ServerInfo(
                Environment.MachineName,
                Environment.OSVersion.ToString(),
                fvi?.ProductVersion ?? assembly?.GetName()?.Version?.ToString(),
                _appName
                );
            auditContextAccessor.AuditContext.SetServerInfo(serverInfo);
        }

        private void PreRequestCollectInfo(IAuditContextAccessor auditContextAccessor,
            ITimeTracker tracker,
            HttpContext context)
        {
            using var _ = tracker.BeginScope("PreRequestCollectInfo");
            CollectRequestInfo(auditContextAccessor, context);
            tracker.Checkpoint("CollectRequestInfo");
            CollectServerInfo(auditContextAccessor);
            tracker.Checkpoint("CollectServerInfo");
        }

        private void PostRequestColllectInfo(IAuditContextAccessor auditContextAccessor,
            HttpContext context, ITimeTracker tracker, Exception? ex = default)
        {
            using var _ = tracker.BeginScope("PostRequestColllectInfo");
            if (auditContextAccessor.AuditContext.AccessRecord.User == null)
            {
                auditContextAccessor.AuditContext.SetUser(GetUser(context));
                tracker.Checkpoint("SetUser");
            }
            var dict = new Dictionary<string, object>();
            if (context.Request.HasFormContentType)
            {
                dict = context.Request.Form.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value);
            }
            if (_routeValues != null)
            {
                foreach (var kvp in _routeValues)
                {
                    if (!dict.ContainsKey(kvp.Key))
                    {
                        dict.Add(kvp.Key, kvp.Value);
                    }
                }
            }
            auditContextAccessor.AuditContext.AccessRecord.Request?.SetData(dict);
            tracker.Checkpoint("SetFormData");

            if (!string.IsNullOrEmpty(_action))
            {
                auditContextAccessor.AuditContext.SetAction(_action);
            }
            
            auditContextAccessor.AuditContext.UpdateResponseInfo(responseInfo =>
            {
                if (ex != null)
                {
                    responseInfo.TrySetMessage(ex.Message);
                    responseInfo.TrySetError(ex.StackTrace ?? ex.ToString());
                }
                else if (context.RequestAborted.IsCancellationRequested)
                {
                    responseInfo.TrySetMessage("Request aborted");
                }

                var status = context.RequestAborted.IsCancellationRequested
                    ? _filter.RequestAbortedStatusCode
                    : context.Response.StatusCode;

                responseInfo.SetResponseInfo(status,
                    JsonConvert.SerializeObject(context.Response.Headers
                        .Where(h => _filter.IsResHeaderMatch(h.Key))
                        .ToDictionary(x => x.Key, x => x.Value)),
                        (long)tracker.ElapsedTime.TotalMilliseconds);
            });
            tracker.Checkpoint("UpdateResponseInfo");
            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized
                || context.Response.StatusCode == StatusCodes.Status403Forbidden)
            {
                auditContextAccessor.AuditContext.AccessRecord.Restricted();
                tracker.Checkpoint("Restricted");
            }
        }

    }
}
