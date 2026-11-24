using Juice.Audit.Commands;
using Juice.Audit.Domain;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit.CommandHandlers
{
    internal class SaveAuditInfoCommandHandler : IRequestHandler<SaveAuditInfoCommand, IOperationResult>
    {
        private readonly IDataAuditRepository _auditRepository;
        private readonly IAccessLogRepository _accessLogRepository;
        private readonly AuditBatchWriter? _writer;

        /// <param name="store">With a batch store (Juice.Audit.EF registers one), the record is written as a batch of
        /// one, so a record sent twice is stored once. Without one, the repositories are used as before.</param>
        public SaveAuditInfoCommandHandler(IDataAuditRepository auditRepository, IAccessLogRepository accessLogRepository,
            IAuditBatchStore? store = null, IAuditWriteErrorClassifier? classifier = null,
            IEnumerable<Collector.IAuditRecordProcessor>? processors = null)
        {
            _accessLogRepository = accessLogRepository;
            _auditRepository = auditRepository;
            _writer = store != null ? new AuditBatchWriter(store, classifier, processors) : null;
        }

        public async ValueTask<IOperationResult> Handle(SaveAuditInfoCommand request, CancellationToken cancellationToken)
        {
            try
            {
                if (_writer != null)
                {
                    var result = await _writer.WriteAsync(
                        new[] { new AuditBatchItem(request.AccessLog, request.DataAuditEntries) }, cancellationToken);
                    return result.ToOperationResult();
                }

                if (request.DataAuditEntries.Length > 0)
                {
                    await _auditRepository.AddRangeAsync(request.DataAuditEntries, cancellationToken);
                }

                return await _accessLogRepository.AddAsync(request.AccessLog, cancellationToken);
            }
            catch (Exception ex)
            {
                return OperationResult.Failed(ex);
            }
        }
    }
}
