using Grpc.Net.ClientFactory;
using Juice.Audit.Api.NotificationHandlers;
using Juice.Audit.Collector;
using Juice.Audit.Domain;
using Juice.Audit.Api.Scopes;
using Juice.Domain.Events;
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
            services.AddScoped<INotificationHandler<TransactionCommitted>, TransactionCommittedHandler>();
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
                .AddAuditCollectorMetrics()
                .AddAuditDbContext(configuration, configureDb)
                .AddEFAuditRepos();
            return services;
        }

        /// <summary>
        /// The collector's processing of every new record before it is stored (<c>Audit:Collector</c> options): the
        /// configured headers masked (<see cref="HeaderRedactionProcessor"/>), long bodies and changes cut
        /// (<see cref="TruncationProcessor"/>), then the app's own processors, added with
        /// <see cref="AuditCollectorBuilder.AddProcessor{T}"/>. Also adds the collector's metrics.
        /// Use with <see cref="ConfigureAuditGrpcHost"/>, or with <see cref="ConfigureAuditDefault"/> in-process.
        /// </summary>
        public static AuditCollectorBuilder AddAuditCollector(this IServiceCollection services,
            Action<AuditCollectorOptions>? configure = default)
        {
            services.AddOptions<AuditCollectorOptions>()
                .Configure<IServiceProvider>((options, sp) =>
                    sp.GetService<IConfiguration>()?.GetSection("Audit:Collector").Bind(options));
            if (configure != null)
            {
                services.Configure(configure);
            }
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditRecordProcessor, HeaderRedactionProcessor>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditRecordProcessor, TruncationProcessor>());
            services.AddAuditCollectorMetrics();
            return new AuditCollectorBuilder(services);
        }

        /// <summary>
        /// The collector's metrics (<c>juice.audit.collector.*</c> on meter <c>Juice.Audit</c>), recorded for every
        /// batch the audit database is written with.
        /// </summary>
        public static IServiceCollection AddAuditCollectorMetrics(this IServiceCollection services)
        {
            services.TryAddSingleton<AuditCollectorMetrics>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditBatchObserver, AuditCollectorMetrics>(
                sp => sp.GetRequiredService<AuditCollectorMetrics>()));
            return services;
        }

        /// <summary>
        /// Use with <c>UseAudit()</c> to enable audit for specific paths
        /// <para>Remote mode: access records are queued and sent in batches to the gRPC Audit service by a background
        /// worker (<c>Audit:Export</c> options). Requests no longer wait for the audit service.</para>
        /// </summary>
        public static IServiceCollection ConfigureAuditGrpcClient(this IServiceCollection services, Action<GrpcClientFactoryOptions> configure)
        {
            services.AddGrpcAuditExporter(configure)
                .AddAuditContextAccessor()
                .AddDataEventHandler();
            return services;
        }

        /// <summary>
        /// Use with <c>UseAudit()</c> to enable audit for specific paths
        /// <para>In-process mode: access records are queued and written in batches to the app's audit database by a
        /// background worker (<c>Audit:Export</c> options). Requests no longer wait for the write.</para>
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
                .AddAuditCollectorMetrics()
                .AddAuditDbContext(configuration, configureDb)
                .AddEFAuditRepos()
                .AddInProcessAuditExporter()
                .AddAuditContextAccessor()
                .AddDataEventHandler();
            return services;
        }
    }
}
