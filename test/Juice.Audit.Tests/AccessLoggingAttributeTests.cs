using FluentAssertions;
using Juice.Audit.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Tests
{
    /// <summary>
    /// <c>[AccessLogging]</c> asks for an access log from an action or page filter, for the request methods in
    /// <see cref="AccessLoggingAttribute.Methods"/> only, when it has any.
    /// </summary>
    public class AccessLoggingAttributeTests
    {
        [Theory]
        [InlineData("GET")]
        [InlineData("POST")]
        public async Task Without_methods_any_request_is_logged_Async(string method)
        {
            var (accessor, http) = Request(method);

            await OnPageAsync(new AccessLoggingAttribute(), http);

            accessor.AuditContext.IsRequestedForAccessLog(200).Should().BeTrue();
        }

        [Theory]
        [InlineData("GET", true)]
        [InlineData("get", true)]
        [InlineData("POST", false)]
        public async Task Page_logs_only_the_given_methods_Async(string method, bool logged)
        {
            var (accessor, http) = Request(method);

            await OnPageAsync(new AccessLoggingAttribute { Methods = new[] { "GET" } }, http);

            accessor.AuditContext.IsRequestedForAccessLog(200).Should().Be(logged);
        }

        [Theory]
        [InlineData("DELETE", true)]
        [InlineData("GET", false)]
        public async Task Action_logs_only_the_given_methods_Async(string method, bool logged)
        {
            var (accessor, http) = Request(method);

            await OnActionAsync(new AccessLoggingAttribute { Methods = new[] { "delete" } }, http);

            accessor.AuditContext.IsRequestedForAccessLog(200).Should().Be(logged);
        }

        [Fact]
        public async Task Name_is_not_set_for_a_method_not_given_Async()
        {
            var (accessor, http) = Request("POST");

            await OnPageAsync(new AccessLoggingAttribute { Name = "ReadReport", Methods = new[] { "GET" } }, http);

            accessor.AuditContext.AccessRecord.Action.Should().NotBe("ReadReport");
        }

        [Fact]
        public async Task Status_codes_still_apply_Async()
        {
            var (accessor, http) = Request("GET");

            await OnActionAsync(new AccessLoggingAttribute(404) { Methods = new[] { "GET" } }, http);

            accessor.AuditContext.IsRequestedForAccessLog(404).Should().BeTrue();
            accessor.AuditContext.IsRequestedForAccessLog(200).Should().BeFalse();
        }

        [Fact]
        public async Task Without_an_audit_context_the_action_still_runs_Async()
        {
            // No UseAudit(): the accessor is registered but never initialized.
            var services = new ServiceCollection().AddAuditContextAccessor().BuildServiceProvider();
            var http = new DefaultHttpContext { RequestServices = services.CreateScope().ServiceProvider };
            http.Request.Method = "GET";

            var invoked = await OnActionAsync(new AccessLoggingAttribute(), http);

            invoked.Should().BeTrue();
        }

        private static (IAuditContextAccessor, HttpContext) Request(string method)
        {
            var services = new ServiceCollection().AddAuditContextAccessor().BuildServiceProvider();
            var scope = services.CreateScope();
            var accessor = scope.ServiceProvider.GetRequiredService<IAuditContextAccessor>();
            // What UseAudit() does before the request runs.
            accessor.Init("Unknown", null);
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.Request.Method = method;
            return (accessor, http);
        }

        private static async Task<bool> OnActionAsync(AccessLoggingAttribute attribute, HttpContext http)
        {
            var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
            var filters = new List<IFilterMetadata> { attribute };
            var controller = new object();
            var context = new ActionExecutingContext(actionContext, filters, new Dictionary<string, object?>(), controller);
            var invoked = false;
            await attribute.OnActionExecutionAsync(context, () =>
            {
                invoked = true;
                return Task.FromResult(new ActionExecutedContext(actionContext, filters, controller));
            });
            return invoked;
        }

        private static async Task OnPageAsync(AccessLoggingAttribute attribute, HttpContext http)
        {
            var pageContext = new PageContext(new ActionContext(http, new RouteData(), new CompiledPageActionDescriptor()));
            var filters = new List<IFilterMetadata> { attribute };
            var handler = new HandlerMethodDescriptor();
            var page = new object();
            var context = new PageHandlerExecutingContext(pageContext, filters, handler, new Dictionary<string, object?>(), page);
            await attribute.OnPageHandlerExecutionAsync(context,
                () => Task.FromResult(new PageHandlerExecutedContext(pageContext, filters, handler, page)));
        }
    }
}
