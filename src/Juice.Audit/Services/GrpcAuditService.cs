using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.Grpc;
using Juice.Audit.Services;

namespace Juice.Audit.Api.Grpc.Services
{
    /// <summary>
    /// Implement IAuditService to persist audit information to Grpc server, one call per record, inside the request
    /// that produced it. Registered only by <c>AddGrpcAuditService()</c>; <c>ConfigureAuditGrpcClient()</c> uses the
    /// background exporter instead.
    /// </summary>
    internal class GrpcAuditService : IAuditService
    {
        /// <summary>Audit must never wait on the network without a deadline (constitution VI).</summary>
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

        private readonly AuditGrpcService.AuditGrpcServiceClient _client;
        public GrpcAuditService(AuditGrpcService.AuditGrpcServiceClient client)
        {
            _client = client;
        }
        public async Task<IOperationResult> PersistAuditInformationAsync(AccessLog accessLog, DataAudit[] auditEntries, CancellationToken token)
        {
            var request = AuditMessageMapper.ToMessage(accessLog, auditEntries);

            var result = await _client.LogAsync(request, deadline: DateTime.UtcNow.Add(Deadline), cancellationToken: token);

            if (!result.Succeeded)
            {
                return OperationResult.Failed(result.Message);
            }
            return OperationResult.Success;
        }
    }
}
