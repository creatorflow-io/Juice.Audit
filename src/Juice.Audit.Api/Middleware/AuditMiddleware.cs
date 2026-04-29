using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using Grpc.Core;
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

        private string GetTraceId(HttpContext context)
        {
            return Activity.Current?.Id ?? context.TraceIdentifier;
        }

        public AuditMiddleware(RequestDelegate next, string appName, AuditFilterOptions options)
        {
            _next = next;
            _appName = appName;
            _filter = options;
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
                    logger.LogDebug("Version: {0}, TraceId: {1}", auditContextAccessor.AuditContext.Version, GetTraceId(context));
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
            bool isMeasureRequested = false;
            try
            {
                using (tracker.BeginScope("Invoke"))
                {
                    await _next(context);
                }


                var status = context.RequestAborted.IsCancellationRequested
                    ? _filter.RequestAbortedStatusCode
                    : context.Response.StatusCode;

                isMeasureRequested = auditContextAccessor.AuditContext.IsRequestedForMeasureLog(tracker.ElapsedTime);
                isTimeExceeded = _filter.ExecutionTimeThreshold.HasValue && tracker.ElapsedTime.TotalMilliseconds > _filter.ExecutionTimeThreshold;

                var isRequested = auditContextAccessor.AuditContext.IsRequestedForAccessLog(status);
                var rule = string.Empty;
                var isFilterMatched = _filter.IsMatch(context.Request.Path, context.Request.Method, status, out rule, out _action, out _routeValues);
                isMatch = isRequested
                    || auditContextAccessor.AuditContext.IsRequestedForAuditLog
                    || isTimeExceeded
                    || isMeasureRequested
                    || isFilterMatched;

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Path {0}. Status: {1}; AccessLog requested: {2}; DataAudit requested: {3};  TimeExceeded: {4}; MeasureRequested {5}; FilterMatched {6} {7}",
                        context.Request.Path, status,
                        isRequested, auditContextAccessor.AuditContext.IsRequestedForAuditLog, isTimeExceeded, isMeasureRequested, isFilterMatched, rule
                        );
                }

                if (!isMatch)
                {
                    return;
                }
                auditContextAccessor.AuditContext.AccessRecord.Metadata["FilterMatched"] = isFilterMatched;
                auditContextAccessor.AuditContext.AccessRecord.Metadata["FilterRule"] = rule;
                auditContextAccessor.AuditContext.AccessRecord.Metadata["AccessLogRequest"] = isRequested;
                auditContextAccessor.AuditContext.AccessRecord.Metadata["DataAuditRequest"] = auditContextAccessor.AuditContext.IsRequestedForAuditLog;
                auditContextAccessor.AuditContext.AccessRecord.Metadata["MeasureRequested"] = isMeasureRequested;
                auditContextAccessor.AuditContext.AccessRecord.Metadata["TimeExceeded"] = isTimeExceeded;
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
                                auditContextAccessor.AuditContext.AuditEntries.ToArray(), default);
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

                if (isTimeExceeded || isMeasureRequested)
                {
                    try
                    {
                        using var _ = tracker.BeginScope("SaveTrackData");
                        var timeRepository = context.RequestServices.GetService<ITimeRepository>();
                        _.Dispose();
                        if (timeRepository != null)
                        {
                            await timeRepository.SaveTrackDataAsync(tracker, GetTraceId(context),
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

            var action = StringUtils.PathToAction(path) ?? "Unknown";

            auditContextAccessor.Init(action, GetUser(context));
            context.Response.Headers.TryAdd("X-Trace-Id", GetTraceId(context));
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
                GetTraceId(context),
                context.Request.Host.ToString()
                )
            ;
            
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
            try
            {
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

            }
            catch { /* ignore */
                tracker.Checkpoint("SetFormDataFailure");
            }

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
