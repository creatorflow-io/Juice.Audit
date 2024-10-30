using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Measurement;
using Juice.Measurement.Internal;
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
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error while initializing the audit context");
            }

            try
            {
                PreRequestCollectInfo(auditContextAccessor, context);
                tracker.Checkpoint("PreRequestCollectInfo");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error while collecting the request information");
            }

            bool isMatch = false;
            try
            {
                await _next(context);
                tracker.Checkpoint("Next");

                var status = context.RequestAborted.IsCancellationRequested
                    ? _filter.RequestAbortedStatusCode
                    : context.Response.StatusCode;

                isMatch = auditContextAccessor.AuditContext.IsRequestedForAccess
                    || auditContextAccessor.AuditContext.IsRequestedForAudit
                    || tracker.ElapsedTime.TotalMilliseconds > _filter.ExecutionTimeThreshold
                    || _filter.IsMatch(context.Request.Path, context.Request.Method, status);
                if (isMatch)
                {
                    try
                    {
                        PostRequestColllectInfo(auditContextAccessor, context, tracker);

                        tracker.Checkpoint("PostRequestColllectInfo");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error while collecting the response information");
                    }
                }
                else
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug("AuditMiddleware.InvokeAsync: Skip CollectResponseInfo because response status does not match");
                    }
                }
            }
            catch (Exception ex)
            {
                isMatch = true;
                try
                {
                    PostRequestColllectInfo(auditContextAccessor, context, tracker, ex);
                    tracker.Checkpoint("PostRequestColllectInfo exception");
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
                        var auditService = context.RequestServices.GetService<IAuditService>();
                        tracker.Checkpoint("GetAuditService");
                        
                        if (auditService != null)
                        {
                            await auditService.PersistAuditInformationAsync(auditContextAccessor.AuditContext.AccessRecord,
                                [.. auditContextAccessor.AuditContext.AuditEntries], default);
                            tracker.Checkpoint("PersistAuditInformation");
                        }else if (logger.IsEnabled(LogLevel.Debug))
                        {
                            logger.LogDebug("AuditMiddleware.InvokeAsync: Skip PersistAuditInformation because IAuditService is not registered.");
                        }
                        if(logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.LogTrace(tracker.ToString(true));
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, $"Error while committing audit information");
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
            var action = context.Request.Path.HasValue
                ? context.Request.Path.Value.Trim('/').Replace("/", "_")
                : "Unknown";

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
                context.Request.Host.HasValue ? context.Request.Host.Value : default
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
            HttpContext context)
        {
            CollectRequestInfo(auditContextAccessor, context);
            CollectServerInfo(auditContextAccessor);
        }

        private void PostRequestColllectInfo(IAuditContextAccessor auditContextAccessor,
            HttpContext context, ITimeTracker tracker, Exception? ex = default)
        {
            if(auditContextAccessor.AuditContext.AccessRecord.User == null)
            {
                auditContextAccessor.AuditContext.SetUser(GetUser(context));
            }
            if (context.Request.HasFormContentType)
            {
                auditContextAccessor.AuditContext.AccessRecord.Request?.SetData(context.Request.Form.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value));
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
                        (long) tracker.ElapsedTime.TotalMilliseconds);
            });

            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized
                || context.Response.StatusCode == StatusCodes.Status403Forbidden)
            {
                auditContextAccessor.AuditContext.AccessRecord.Restricted();
            }
        }

    }
}
