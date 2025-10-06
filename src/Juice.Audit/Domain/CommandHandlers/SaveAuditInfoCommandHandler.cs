using Juice.Audit.Commands;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit.CommandHandlers
{
    internal class SaveAuditInfoCommandHandler : IRequestHandler<SaveAuditInfoCommand, IOperationResult>
    {
        private readonly IDataAuditRepository _auditRepository;
        private readonly IAccessLogRepository _accessLogRepository;

        public SaveAuditInfoCommandHandler(IDataAuditRepository auditRepository, IAccessLogRepository accessLogRepository)
        {
            _accessLogRepository = accessLogRepository;
            _auditRepository = auditRepository;
        }

        public async ValueTask<IOperationResult> Handle(SaveAuditInfoCommand request, CancellationToken cancellationToken)
        {
            try
            {
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
