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
        /// The methods that will trigger the access log, applicable only for page.
        /// </summary>
        public string[] Methods { get; set; } = Array.Empty<string>();
        public AccessLoggingAttribute(params int[] statusCodes)
        {
            _statusCodes = statusCodes;
        }

        private void RequestAccessLog(HttpContext context)
        {
            var contextAccessor = context.RequestServices.GetService<IAuditContextAccessor>();
            if (contextAccessor != null)
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
