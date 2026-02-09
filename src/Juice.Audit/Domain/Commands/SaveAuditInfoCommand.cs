using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit.Commands
{
    public record SaveAuditInfoCommand : MessageBase, IRequest<IOperationResult>
    {
        public AccessLog AccessLog { get; private set; }
        public DataAudit[] DataAuditEntries { get; private set; }

        public SaveAuditInfoCommand(AccessLog accessLog, params DataAudit[] dataAuditEntries)
        {
            AccessLog = accessLog;
            DataAuditEntries = dataAuditEntries;
        }
    }
}
