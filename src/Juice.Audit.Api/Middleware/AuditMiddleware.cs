using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Juice.Audit.Domain.AccessLogAggregate;
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

            var totalTimeTracker = new Stopwatch();
            var timeTracker = new Stopwatch();
            var dbg = logger.IsEnabled(LogLevel.Debug);
            timeTracker.Start();
            totalTimeTracker.Start();

            try
            {
                InitAuditContext(auditContextAccessor, context);
                if (dbg)
                {
                    logger.LogDebug("AuditMiddleware.InvokeAsync: InitAuditContext {0}", timeTracker.ElapsedMilliseconds);
                    timeTracker.Restart();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error while initializing the audit context");
            }

            try
            {
                PreRequestCollectInfo(auditContextAccessor, context);
                if (dbg)
                {
                    logger.LogDebug("AuditMiddleware.InvokeAsync: PreRequestCollectInfo {0}", timeTracker.ElapsedMilliseconds);
                    timeTracker.Restart();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error while collecting the request information");
            }

            bool isMatch = false;
            try
            {
                await _next(context);
                if (dbg)
                {
                    logger.LogDebug("AuditMiddleware.InvokeAsync: _next {0}", timeTracker.ElapsedMilliseconds);
                    timeTracker.Restart();
                }

                var status = context.RequestAborted.IsCancellationRequested
                    ? _filter.RequestAbortedStatusCode
                    : context.Response.StatusCode;

                isMatch = auditContextAccessor.AuditContext.IsRequestedForAccess
                    || auditContextAccessor.AuditContext.IsRequestedForAudit
                    || _filter.IsMatch(context.Request.Path, context.Request.Method, status);
                if (isMatch)
                {
                    try
                    {
                        PostRequestColllectInfo(auditContextAccessor, context, totalTimeTracker.ElapsedMilliseconds);
                        if (dbg)
                        {
                            logger.LogDebug("AuditMiddleware.InvokeAsync: CollectResponseInfo {0}", timeTracker.ElapsedMilliseconds);
                            timeTracker.Restart();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error while collecting the response information");
                    }
                }
                else
                {
                    if (dbg)
                    {
                        logger.LogDebug("AuditMiddleware.InvokeAsync: Skip CollectResponseInfo because response status does not match. AccessLog requested: {0}; DataAudit requested: {1}",
                            auditContextAccessor.AuditContext.IsRequestedForAccess, auditContextAccessor.AuditContext.IsRequestedForAudit);
                    }
                }
            }
            catch (Exception ex)
            {
                isMatch = true;
                try
                {
                    PostRequestColllectInfo(auditContextAccessor, context, totalTimeTracker.ElapsedMilliseconds, ex);
                    if (dbg)
                    {
                        logger.LogDebug("AuditMiddleware.InvokeAsync: CollectResponseError {0}", timeTracker.ElapsedMilliseconds);
                        timeTracker.Restart();
                    }
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
                        if (dbg)
                        {
                            logger.LogDebug("AuditMiddleware.InvokeAsync: Get IAuditService {0}", timeTracker.ElapsedMilliseconds);
                            timeTracker.Restart();
                        }
                        if (auditService != null)
                        {
                            await auditService.PersistAuditInformationAsync(auditContextAccessor.AuditContext.AccessRecord,
                                auditContextAccessor.AuditContext.AuditEntries.ToArray(), default);
                        }
                        if (dbg)
                        {
                            logger.LogDebug("AuditMiddleware.InvokeAsync: CommitAuditInformationAsync {0}", timeTracker.ElapsedMilliseconds);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, $"Error while committing audit information");
                    }
                }
                timeTracker.Stop();
                totalTimeTracker.Stop();
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

            // remove the id (int or guid) from the path
            var path = context.Request.Path.HasValue
                ? context.Request.Path.Value : "";
            path = Regex.Replace(path, @"\/[a-f0-9]{8}-([a-f0-9]{4}-){3}[a-f0-9]{12}", "/{id}", RegexOptions.IgnoreCase);
            path = Regex.Replace(path, @"\/[0-9]+", "/{id}", RegexOptions.IgnoreCase);

            var requestInfo = new RequestInfo(
                context.Request.Method,
                path,
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
            HttpContext context, long elapsed, Exception? ex = default)
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
                    elapsed);

            });
            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized
                || context.Response.StatusCode == StatusCodes.Status403Forbidden)
            {
                auditContextAccessor.AuditContext.AccessRecord.Restricted();
            }
        }

    }
}
