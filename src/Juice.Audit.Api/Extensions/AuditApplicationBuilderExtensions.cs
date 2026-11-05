using Juice.Audit.Api.Grpc.Services;
using Juice.Audit.AspNetCore.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Juice.Audit.AspNetCore.Extensions
{
    public static class AuditApplicationBuilderExtensions
    {
        /// <summary>
        /// Records an access log for POST, PUT, PATCH and DELETE requests (<see cref="AuditFilterOptions.DefaultMethods"/>),
        /// and for any request marked <c>[AccessLogging]</c>, asked for in code, that changed data, threw, or ran past
        /// a time threshold. <paramref name="configure"/> adds rules on top of the default one, or calls
        /// <c>Clear()</c> to drop it.
        /// <para>We should put <c>UseAudit()</c> after <c>UseStaticFiles()</c> to avoid static file requests</para>
        /// </summary>
        /// <param name="app"></param>
        /// <param name="configure"></param>
        /// <returns></returns>
        public static IApplicationBuilder UseAudit(this IApplicationBuilder app,
            string appName, Action<AuditFilterOptions>? configure = default)
        {
            var options = AuditFilterOptions.CreateDefault();
            configure?.Invoke(options);

            app.UseMiddleware<AuditMiddleware>(appName, options);

            return app;
        }

        /// <summary>
        /// Records an access log for POST, PUT, PATCH and DELETE requests by default; see the
        /// <see cref="IApplicationBuilder"/> overload.
        /// <para>We should put <c>UseAudit()</c> after <c>UseStaticFiles()</c> to avoid static file requests</para>
        /// </summary>
        /// <param name="app"></param>
        /// <param name="configure"></param>
        /// <returns></returns>
        public static WebApplication UseAudit(this WebApplication app,
            string appName, Action<AuditFilterOptions>? configure = default)
        {
            ((IApplicationBuilder)app).UseAudit(appName, configure);
            return app;
        }

        /// <summary>
        /// Map the gRPC Audit server to handle audit messages from clients
        /// </summary>
        /// <param name="app"></param>
        /// <returns></returns>
        public static IEndpointRouteBuilder MapAuditGrpcServer(this IEndpointRouteBuilder app)
        {
            app.MapGrpcService<AuditGrpcServer>();
            return app;
        }

    }
}
