using Grpc.Net.ClientFactory;
using Juice;
using Juice.Audit;
using Juice.Audit.Api.Grpc.Services;
using Juice.Audit.CommandHandlers;
using Juice.Audit.Commands;
using Juice.Audit.Export;
using Juice.Audit.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class AuditServiceCollectionExtensions
    {
        public static IServiceCollection AddAuditContextAccessor(this IServiceCollection services)
        {
            services.AddScoped<IAuditContextAccessor, DefaultAuditContextAccessor>();
            return services;
        }

        /// <summary>
        /// Persist audit information by send a SaveAuditInfoCommand to the IMediator (data will store to repositories directly)
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection AddDefaultAuditService(this IServiceCollection services)
        {
            services.AddScoped<IAuditService, DefaultAuditService>();
            return services;
        }

        /// <summary>
        /// Persist audit information by send an message to the gRPC Audit service (data will store to repositories remotely)
        /// </summary>
        /// <param name="services"></param>
        /// <param name="grpcEndpoint"></param>
        /// <returns></returns>
        public static IServiceCollection AddGrpcAuditService(this IServiceCollection services, Action<GrpcClientFactoryOptions> comfigure)
        {
            services.AddGrpcClient<Juice.Audit.Grpc.AuditGrpcService.AuditGrpcServiceClient>(comfigure);

            services.AddScoped<IAuditService, GrpcAuditService>();
            return services;
        }


        /// <summary>
        /// Access records leave the app in the background: finishing a scope queues its record
        /// (<see cref="AuditExportQueue"/>), and <see cref="AuditExportWorker"/>, a hosted service, sends the queue in
        /// batches through the registered <see cref="IAuditExporter"/>. Replaces the <see cref="IAuditService"/>.
        /// Options are bound from <c>Audit:Export</c>, then <paramref name="configure"/>.
        /// <para>The worker runs with the host. Without one (a bare service provider), set
        /// <see cref="AuditExportOptions.Synchronous"/>.</para>
        /// </summary>
        public static IServiceCollection AddAuditExport(this IServiceCollection services,
            Action<AuditExportOptions>? configure = default)
        {
            services.AddOptions<AuditExportOptions>()
                .Configure<IServiceProvider>((options, sp) =>
                    sp.GetService<IConfiguration>()?.GetSection("Audit:Export").Bind(options))
                // A batch of 0 would never fill: the worker would spin, sending nothing.
                .Validate(options => options.MaxBatchSize > 0, "Audit:Export:MaxBatchSize must be greater than 0.")
                .Validate(options => options.QueueCapacity > 0, "Audit:Export:QueueCapacity must be greater than 0.");
            if (configure != null)
            {
                services.Configure(configure);
            }
            services.TryAddSingleton<AuditExportMetrics>();
            services.TryAddSingleton<AuditDropReporter>();
            services.TryAddSingleton<AuditExportQueue>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AuditExportWorker>());
            services.Replace(ServiceDescriptor.Scoped<IAuditService, QueuedAuditService>());
            return services;
        }

        /// <summary>
        /// Remote mode: the export worker sends batches to the audit service over gRPC (<c>LogBatch</c>, with a
        /// deadline; one record per call to a collector before 10.7). See <see cref="AddAuditExport"/>.
        /// </summary>
        public static IServiceCollection AddGrpcAuditExporter(this IServiceCollection services,
            Action<GrpcClientFactoryOptions> configure)
        {
            // The client factory applies configure when the export worker first needs the client, so a delegate may
            // read what is set after registration. Options that can't work are logged then (AuditExportWorker).
            services.AddGrpcClient<Juice.Audit.Grpc.AuditGrpcService.AuditGrpcServiceClient>(configure);
            services.TryAddSingleton<GrpcExportState>();
            services.Replace(ServiceDescriptor.Scoped<IAuditExporter, GrpcAuditExporter>());
            return services.AddAuditExport();
        }

        /// <summary>
        /// In-process mode: the export worker writes batches through the app's own mediator
        /// (<see cref="SaveAuditBatchCommand"/>) to the audit database the app carries. See <see cref="AddAuditExport"/>.
        /// </summary>
        public static IServiceCollection AddInProcessAuditExporter(this IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Scoped<IAuditExporter, InProcessAuditExporter>());
            return services.AddAuditExport();
        }

        public static IServiceCollection AddAuditCommandHandlers(this IServiceCollection services)
        {
            services.AddScoped<IRequestHandler<SaveAuditInfoCommand, IOperationResult>, SaveAuditInfoCommandHandler>();
            services.AddScoped<IRequestHandler<SaveAuditBatchCommand, AuditBatchResult>, SaveAuditBatchCommandHandler>();
            return services;
        }
    }
}
