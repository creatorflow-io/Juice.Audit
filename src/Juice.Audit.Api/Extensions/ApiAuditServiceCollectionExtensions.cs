using Grpc.Net.ClientFactory;
using Juice.Audit.Api.NotificationHandlers;
using Juice.Audit.Api.Scopes;
using Juice.EF;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ApiAuditServiceCollectionExtensions

    {
        /// <summary>
        /// Handle DataEvent from <c>INotificationHandler<DataEvent></c> to fulfill audit information
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection AddDataEventHandler(this IServiceCollection services)
        {
            services.AddScoped(typeof(INotificationHandler<>), typeof(DataEvenNotificationtHandler<>));
            return services;
        }

        /// <summary>
        /// Opens an audit scope around mediator requests and notifications that run outside an audited HTTP request or
        /// message: background jobs, scheduled tasks, local-channel notifications. The data they change is saved under
        /// the current trace id, by <c>UseAudit()</c>'s rules (see <see cref="AuditScopeOptions"/>). Inside an audited
        /// request or message they join its scope. Use with <c>ConfigureAuditDefault()</c> or
        /// <c>ConfigureAuditGrpcClient()</c>.
        /// </summary>
        public static IServiceCollection AddMediatorAudit(this IServiceCollection services,
            Action<AuditScopeOptions>? configure = default)
        {
            services.AddAuditScopeRunner(configure);
            services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(AuditRequestBehavior<,>)));
            services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehavior<>), typeof(AuditRequestBehavior<>)));
            services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(INotificationPipelineBehavior<>),
                typeof(AuditNotificationBehavior<>)));
            return services;
        }

        /// <summary>
        /// Adds the <see cref="AuditScopeRunner"/> that opens audit scopes outside an HTTP request, for
        /// <see cref="AddMediatorAudit"/> and <c>AddMessageAudit()</c> (Juice.Audit.Messaging). Each call's
        /// <paramref name="configure"/> applies to the same <see cref="AuditScopeOptions"/>.
        /// </summary>
        public static IServiceCollection AddAuditScopeRunner(this IServiceCollection services,
            Action<AuditScopeOptions>? configure = default)
        {
            services.AddOptions<AuditScopeOptions>();
            if (configure != null)
            {
                services.Configure(configure);
            }
            services.TryAddScoped<AuditScopeRunner>();
            return services;
        }

        /// <summary>
        /// Use with <c>MapAuditGrpcServer()</c> to handle audit data from gRPC client
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <param name="configureDb"></param>
        /// <returns></returns>
        public static IServiceCollection ConfigureAuditGrpcHost(this IServiceCollection services,
            IConfiguration configuration,
            Action<DbOptions>? configureDb = default)
        {
            services.AddDefaultAuditService()
                .AddAuditCommandHandlers()
                .AddAuditDbContext(configuration, configureDb)
                .AddEFAuditRepos();
            return services;
        }

        /// <summary>
        /// Use with <c>UseAudit()</c> to enable audit for specific paths
        /// <para>Store audit information to gRPC Audit service</para></para>
        /// </summary>
        /// <param name="services"></param>
        /// <param name="grpcEndpoint"></param>
        /// <returns></returns>
        public static IServiceCollection ConfigureAuditGrpcClient(this IServiceCollection services, Action<GrpcClientFactoryOptions> configure)
        {
            services.AddGrpcAuditService(configure)
                .AddAuditContextAccessor()
                .AddDataEventHandler();
            return services;
        }

        /// <summary>
        /// Use with <c>UseAudit()</c> to enable audit for specific paths
        /// <para>Store audit information to EF repository directly</para>
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <param name="configureDb"></param>
        /// <returns></returns>
        public static IServiceCollection ConfigureAuditDefault(this IServiceCollection services,
                       IConfiguration configuration,
                       Action<DbOptions>? configureDb = default)
        {
            services.AddDefaultAuditService()
                .AddAuditCommandHandlers()
                .AddAuditDbContext(configuration, configureDb)
                .AddEFAuditRepos()
                .AddAuditContextAccessor()
                .AddDataEventHandler();
            return services;
        }
    }
}
