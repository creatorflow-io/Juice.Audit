using Grpc.Core;
using Juice.Audit.Commands;
using Juice.Audit.Grpc;
using Juice.Audit.Services;
using GrpcBatchResult = Juice.Audit.Grpc.AuditBatchResult;
using GrpcRecordResult = Juice.Audit.Grpc.AuditRecordResult;

namespace Juice.Audit.Api.Grpc.Services
{
    internal class AuditGrpcServer : AuditGrpcService.AuditGrpcServiceBase
    {
        private readonly IMediator _mediator;
        public AuditGrpcServer(IMediator mediator)
        {
            _mediator = mediator;
        }

        /// <summary>
        /// Access records in batches (10.7). Written as the root tenant, each record under its own tenant; the result
        /// tells, per record, whether it was stored, was already stored, was refused, or can be retried.
        /// </summary>
        public override async Task<GrpcBatchResult> LogBatch(AuditBatchRequest request, ServerCallContext context)
        {
            var reply = new GrpcBatchResult();
            var items = new List<AuditBatchItem>(request.Items.Count);
            foreach (var message in request.Items)
            {
                try
                {
                    items.Add(AuditMessageMapper.FromMessage(message));
                }
                catch (Exception ex)
                {
                    // An invalid record is refused alone; retrying it won't help.
                    foreach (var id in Ids(message))
                    {
                        reply.Results.Add(new GrpcRecordResult
                        {
                            Id = id,
                            Outcome = GrpcRecordResult.Types.Outcome.Rejected,
                            Reason = ex.Message
                        });
                    }
                }
            }
            if (items.Count > 0)
            {
                var result = await _mediator.Send(new SaveAuditBatchCommand(items), context.CancellationToken);
                reply.Results.AddRange(result.Results.Select(AuditMessageMapper.ToMessage));
            }
            return reply;
        }

        private static IEnumerable<string> Ids(AuditRequest message)
            => message.AuditEntries.Select(e => e.Id).Prepend(message.AccessLog?.Id)
                .Where(id => !string.IsNullOrEmpty(id))!;

        /// <summary>One record per call, for clients before 10.7.</summary>
        public override async Task<AuditOperationResult> Log(AuditRequest request, ServerCallContext context)
        {
            try
            {
                var item = AuditMessageMapper.FromMessage(request);
                var saveAuditCommand = new SaveAuditInfoCommand(item.AccessLog!, item.DataAudits.ToArray());
                var rs = await _mediator.Send(saveAuditCommand, context.CancellationToken);
                if (!rs.Succeeded)
                {
                    return new AuditOperationResult()
                    {
                        Succeeded = false,
                        Message = rs.Message
                    };
                }
                return new AuditOperationResult()
                {
                    Succeeded = true
                };
            }
            catch (Exception ex)
            {
                return new AuditOperationResult()
                {
                    Succeeded = false,
                    Message = ex.Message
                };
            }
        }
    }
}
