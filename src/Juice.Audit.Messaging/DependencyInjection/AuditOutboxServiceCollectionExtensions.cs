using Juice.Audit;
using Juice.Audit.Export;
using Juice.Audit.Messaging;
using Juice.Audit.Messaging.Collector;
using Juice.Audit.Messaging.Outbox;
using Juice.EventBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class AuditOutboxServiceCollectionExtensions
    {
        /// <summary>
        /// Audits the data changes of <typeparamref name="TContext"/>'s transactions through its outbox: they are saved
        /// with the business change, in a <see cref="DataChangesAudited"/> event, and reach the audit service if and
        /// only if the transaction committed. Call once per audited context that runs under
        /// <c>TransactionBehavior</c> and has an outbox; route domain <c>juice.audit</c> in the publishing policy.
        /// Changes the outbox can't carry stay best effort, counted in <c>juice.audit.outbox.entries</c>.
        /// <para>Options are bound from <c>Audit:Outbox</c>, then <paramref name="configure"/>.</para>
        /// </summary>
        public static IServiceCollection AddAuditOutbox<TContext>(this IServiceCollection services,
            Action<AuditOutboxOptions>? configure = default)
            where TContext : DbContext
        {
            services.AddOptions<AuditOutboxOptions>()
                .Configure<IServiceProvider>((options, sp) =>
                    sp.GetService<IConfiguration>()?.GetSection("Audit:Outbox").Bind(options));
            if (configure != null)
            {
                services.Configure(configure);
            }
            services.TryAddSingleton<AuditExportMetrics>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuditOutboxBinding, AuditOutboxBinding<TContext>>());
            services.TryAddScoped<IDataChangeSink, OutboxDataChangeSink>();
            return services;
        }

        /// <summary>
        /// The collector's side: stores the <see cref="DataChangesAudited"/> events the apps' outboxes deliver, each
        /// change once. Use on the consumer of the queue or channel <c>juice.audit</c> is routed to.
        /// </summary>
        public static IConsumerBuilder SubscribeAuditDataChanges(this IConsumerBuilder builder)
            => builder.Subscribe<DataChangesAudited, DataChangesAuditedHandler>();
    }
}
