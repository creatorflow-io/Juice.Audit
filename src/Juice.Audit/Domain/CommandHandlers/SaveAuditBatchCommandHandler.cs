using System.Diagnostics;
using Juice.Audit.Commands;
using Juice.Audit.Domain;

namespace Juice.Audit.CommandHandlers
{
    /// <summary>The collector's write: each record once, the batch in one save (see <see cref="AuditBatchWriter"/>).</summary>
    internal sealed class SaveAuditBatchCommandHandler : IRequestHandler<SaveAuditBatchCommand, AuditBatchResult>
    {
        private readonly AuditBatchWriter _writer;
        private readonly IAuditBatchObserver[] _observers;

        public SaveAuditBatchCommandHandler(IAuditBatchStore store, IAuditWriteErrorClassifier? classifier = null,
            IEnumerable<IAuditBatchObserver>? observers = null, IEnumerable<Collector.IAuditRecordProcessor>? processors = null)
        {
            _writer = new AuditBatchWriter(store, classifier, processors);
            _observers = observers?.ToArray() ?? Array.Empty<IAuditBatchObserver>();
        }

        public async ValueTask<AuditBatchResult> Handle(SaveAuditBatchCommand request, CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            var result = await _writer.WriteAsync(request.Items, cancellationToken);
            foreach (var observer in _observers)
            {
                try
                {
                    observer.BatchWritten(request.Items, result, started.Elapsed);
                }
                catch
                {
                    // Metrics must not fail the write.
                }
            }
            return result;
        }
    }
}
