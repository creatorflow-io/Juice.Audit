using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.AspNetCore.Mvc.Filters
{
    /// <summary>
    /// Request access logging for an action or a page.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class AccessLoggingAttribute : Attribute, IAsyncActionFilter, IAsyncPageFilter
    {
        /// <summary>
        /// The name of the action that will be logged.
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// The status codes that will trigger the access log.
        /// </summary>
        private int[] _statusCodes;
        /// <summary>
        /// The HTTP methods that will trigger the access log, compared ignoring case; empty (the default) for any.
        /// Mostly for a page or controller, where one attribute covers handlers for several methods:
        /// <c>[AccessLogging(Methods = new[] { "GET" })]</c> on a page model records its <c>OnGet</c> only.
        /// </summary>
        public string[] Methods { get; set; } = Array.Empty<string>();
        public AccessLoggingAttribute(params int[] statusCodes)
        {
            _statusCodes = statusCodes;
        }

        private bool IsMethodMatch(string method)
            => Methods.Length == 0 || Methods.Contains(method, System.StringComparer.OrdinalIgnoreCase);

        private void RequestAccessLog(HttpContext context)
        {
            if (!IsMethodMatch(context.Request.Method))
            {
                return;
            }
            var contextAccessor = context.RequestServices.GetService<IAuditContextAccessor>();
            // Not initialized when the app has no UseAudit(), or this request is outside it: nothing to record.
            if (contextAccessor != null && contextAccessor.IsInitialized)
            {
                contextAccessor.AuditContext.RequestAccessLog(_statusCodes);
                if (!string.IsNullOrEmpty(Name))
                {
                    contextAccessor.AuditContext.SetAction(Name);
                }
            }
        }

        public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            RequestAccessLog(context.HttpContext);
            return next();
        }

        public Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            RequestAccessLog(context.HttpContext);
            return next();
        }

        public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;
    }
}
