using Juice.Audit.Commands;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.MediatR;
using Juice.Messaging;
using Microsoft.Extensions.Logging;

namespace Juice.Audit.Messaging.Collector
{
    /// <summary>
    /// The collector's consumer of <see cref="DataChangesAudited"/>: stores each change once, by its id, so a message
    /// delivered twice changes nothing. A change that is invalid is skipped and logged; the others are stored. A
    /// transient failure throws, so the transport delivers the message again.
    /// </summary>
    internal sealed class DataChangesAuditedHandler : IIntegrationEventHandler<DataChangesAudited>
    {
        private readonly IMediator _mediator;
        private readonly ILogger _logger;

        public DataChangesAuditedHandler(IMediator mediator, ILogger<DataChangesAuditedHandler> logger)
        {
            _mediator = mediator;
            _logger = logger;
        }

        public async Task HandleAsync(DataChangesAudited @event)
        {
            var entries = new List<DataAudit>(@event.Entries.Count);
            foreach (var change in @event.Entries)
            {
                if (TryCreate(change, @event, out var entry))
                {
                    entries.Add(entry);
                }
            }
            if (entries.Count == 0)
            {
                return;
            }

            var result = await _mediator.Send(new SaveAuditBatchCommand(new[] { new AuditBatchItem(null, entries) }));
            foreach (var rejected in result.Results.Where(r => r.Outcome == AuditRecordOutcome.Rejected))
            {
                _logger.LogWarning("Data change {Id} of transaction {TransactionId} ({App}) was refused: {Reason}",
                    rejected.Id, @event.TransactionId, @event.App, rejected.Reason);
            }
            if (result.AnyRetryable)
            {
                var retryable = result.Results.First(r => r.Outcome == AuditRecordOutcome.Retryable);
                throw new InvalidOperationException(
                    $"Data changes of transaction {@event.TransactionId} could not be stored yet: {retryable.Reason}");
            }
        }

        private bool TryCreate(DataChangeEntry change, DataChangesAudited @event, out DataAudit entry)
        {
            entry = default!;
            if (change.Id == Guid.Empty || string.IsNullOrWhiteSpace(change.Tbl))
            {
                _logger.LogWarning("A data change of transaction {TransactionId} ({App}) has no id or table; skipped.",
                    @event.TransactionId, @event.App);
                return false;
            }
            try
            {
                entry = new DataAudit(change.User, change.DateTime, change.Action, change.Db, change.Schema, change.Tbl,
                    change.Kvps, change.Changes, change.TraceId, change.TenantId ?? @event.TenantId)
                {
                    Id = change.Id
                };
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Data change {Id} of transaction {TransactionId} ({App}) is invalid; skipped.",
                    change.Id, @event.TransactionId, @event.App);
                return false;
            }
        }
    }
}
