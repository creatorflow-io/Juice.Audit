using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Audit.Collector
{
    /// <summary>Returned by <c>AddAuditCollector()</c>, to add the app's own record processors.</summary>
    public sealed class AuditCollectorBuilder
    {
        public AuditCollectorBuilder(IServiceCollection services) => Services = services;

        public IServiceCollection Services { get; }

        /// <summary>
        /// Adds <typeparamref name="T"/>, scoped, to the processors every new record goes through, in
        /// <see cref="IAuditRecordProcessor.Order"/> with the built-ins (100 and 200).
        /// </summary>
        public AuditCollectorBuilder AddProcessor<T>() where T : class, IAuditRecordProcessor
        {
            Services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuditRecordProcessor, T>());
            return this;
        }
    }
}
