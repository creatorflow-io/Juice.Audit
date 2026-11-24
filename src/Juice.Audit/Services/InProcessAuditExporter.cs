using Juice.Audit.Commands;
using Juice.Audit.Export;
using Microsoft.Extensions.Options;

namespace Juice.Audit.Services
{
    /// <summary>
    /// In-process mode: the batch goes to <see cref="SaveAuditBatchCommand"/> on the app's own mediator, which writes
    /// it to the audit database the app carries. Resolved in the worker's scope for each batch.
    /// </summary>
    internal sealed class InProcessAuditExporter : IAuditExporter
    {
        private readonly IMediator _mediator;
        private readonly AuditExportOptions _options;

        public InProcessAuditExporter(IMediator mediator, IOptions<AuditExportOptions> options)
        {
            _mediator = mediator;
            _options = options.Value;
        }

        public async Task<AuditBatchResult> ExportAsync(IReadOnlyList<AuditExportItem> batch,
            CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.ExportTimeout);
            var command = new SaveAuditBatchCommand(
                batch.Select(i => new AuditBatchItem(i.AccessRecord, i.BestEffortEntries)).ToList());
            try
            {
                return await _mediator.Send(command, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The audit database did not answer within {_options.ExportTimeout}.");
            }
        }
    }
}
