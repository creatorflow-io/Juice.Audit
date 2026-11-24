using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.Export;
using Juice.MultiTenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Audit.Services
{
    /// <summary>
    /// The <see cref="IAuditService"/> of both modes: finishing a scope only queues its access record (with the data
    /// changes carried with it); the export worker sends it. Never waits on I/O, except in
    /// <see cref="AuditExportOptions.Synchronous"/> mode, which exports inside the scope for tests.
    /// </summary>
    internal sealed class QueuedAuditService : IAuditService
    {
        private readonly AuditExportQueue _queue;
        private readonly AuditExportMetrics _metrics;
        private readonly IServiceProvider _services;
        private readonly AuditExportOptions _options;

        public QueuedAuditService(AuditExportQueue queue, AuditExportMetrics metrics, IServiceProvider services,
            IOptions<AuditExportOptions> options)
        {
            _queue = queue;
            _metrics = metrics;
            _services = services;
            _options = options.Value;
        }

        public async Task<IOperationResult> PersistAuditInformationAsync(AccessLog accessLog, DataAudit[] auditEntries,
            CancellationToken token)
        {
            // The record is written later, in the worker's scope, which has no tenant: an in-process write would store
            // a record without one as the root tenant's. Stamp this scope's tenant now, as the write used to.
            var tenantId = _services.GetService<ITenantAccessor>()?.Tenant?.Id;
            if (!string.IsNullOrEmpty(tenantId))
            {
                accessLog.SetTenant(tenantId);
                foreach (var entry in auditEntries)
                {
                    entry.SetTenant(tenantId);
                }
            }
            var item = new AuditExportItem(accessLog, auditEntries);
            if (!_options.Synchronous)
            {
                // A full queue drops the record and counts it (juice.audit.export.records{outcome=dropped}). Reporting
                // a failure here would make every scope log a warning of its own, once per dropped record.
                _queue.TryEnqueue(item);
                return OperationResult.Success;
            }

            try
            {
                var result = await _services.GetRequiredService<IAuditExporter>().ExportAsync(new[] { item }, token);
                var access = result.Results.FirstOrDefault(r => r.Id == accessLog.Id);
                if (access?.Outcome == Commands.AuditRecordOutcome.Stored)
                {
                    _metrics.Exported(1);
                }
                else if (access?.Outcome == Commands.AuditRecordOutcome.Duplicate)
                {
                    _metrics.Duplicate(1);
                }
                return result.ToOperationResult();
            }
            catch (Exception ex)
            {
                return OperationResult.Failed(ex);
            }
        }
    }
}
