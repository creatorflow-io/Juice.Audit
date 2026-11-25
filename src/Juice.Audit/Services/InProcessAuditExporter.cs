using Juice.Audit.Commands;
using Juice.Audit.Export;
using Juice.Messaging;
using Microsoft.Extensions.Options;

namespace Juice.Audit.Services
{
    /// <summary>
    /// In-process mode: the batch goes to <see cref="SaveAuditBatchCommand"/> on the app's own mediator, which writes
    /// it to the audit database the app carries. Resolved in the worker's scope for each batch.
    /// </summary>
    internal sealed class InProcessAuditExporter : IAuditExporter
    {
        /// <summary>The <see cref="MessageContextData.Source"/> of the context a batch is saved in.</summary>
        internal const string ContextSource = "Juice.Audit.Export";

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
            // The worker sends from its own thread, not a request or a message: an entry point of its own. The app's
            // pipeline may need the context (a TransactionBehavior that saves its outbox for every command does), and
            // without it every batch fails and is dropped. Set here, it stays with this call (an AsyncLocal).
            if (!MessageContext.IsInitialized)
            {
                MessageContext.Initialize(correlationId: Guid.NewGuid().ToString(), causationId: null,
                    executionId: Guid.NewGuid().ToString(), source: ContextSource);
            }
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
