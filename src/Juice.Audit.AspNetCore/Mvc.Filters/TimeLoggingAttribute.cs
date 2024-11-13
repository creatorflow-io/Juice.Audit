using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.AspNetCore.Mvc.Filters
{
    /// <summary>
    /// Request time logging for an action or a page.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class TimeLoggingAttribute : Attribute, IAsyncActionFilter, IAsyncPageFilter
    {
        /// <summary>
        /// The name of the action that will be logged.
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// The scope id of the action that will be logged.
        /// </summary>
        public string? ScopeId { get; set; }

        /// <summary>
        /// The time threshold to trigger the time loging.
        /// </summary>
        private TimeSpan? _threshold;

        public TimeLoggingAttribute(int thresholdMs)
        {
            _threshold = TimeSpan.FromMilliseconds(thresholdMs);
        }

        public TimeLoggingAttribute()
        {
        }


        private void RequestMeasureLog(HttpContext context)
        {
            var contextAccessor = context.RequestServices.GetService<IAuditContextAccessor>();
            if (contextAccessor != null)
            {
                contextAccessor.AuditContext.RequestMeasureLog(_threshold);
                if (!string.IsNullOrEmpty(Name))
                {
                    contextAccessor.AuditContext.SetAction(Name);
                }
                if (!string.IsNullOrEmpty(ScopeId))
                {
                    contextAccessor.AuditContext.Items["ScopeId"] = ScopeId;
                }
            }
        }

        public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            RequestMeasureLog(context.HttpContext);
            return next();
        }

        public Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            RequestMeasureLog(context.HttpContext);
            return next();
        }

        public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;
    }
}
