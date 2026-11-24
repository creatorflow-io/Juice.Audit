using System.Data.Common;
using Juice.Audit.Collector;
using Juice.Audit.Commands;
using Juice.Audit.Domain;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit.CommandHandlers
{
    /// <summary>
    /// Stores audit records once each. Ids already stored (or repeated in the batch) are duplicates; the rest is
    /// written in one save. When that save fails for a reason other than a transient one, the records are written one
    /// by one, so a single bad record or a concurrent writer of the same ids doesn't fail the others.
    /// </summary>
    internal sealed class AuditBatchWriter
    {
        private readonly IAuditBatchStore _store;
        private readonly IAuditWriteErrorClassifier _classifier;
        private readonly IAuditRecordProcessor[] _processors;

        public AuditBatchWriter(IAuditBatchStore store, IAuditWriteErrorClassifier? classifier,
            IEnumerable<IAuditRecordProcessor>? processors = null)
        {
            _store = store;
            _classifier = classifier ?? DefaultAuditWriteErrorClassifier.Instance;
            _processors = processors?.OrderBy(p => p.Order).ToArray() ?? Array.Empty<IAuditRecordProcessor>();
        }

        public async Task<AuditBatchResult> WriteAsync(IReadOnlyList<AuditBatchItem> items,
            CancellationToken cancellationToken)
        {
            var accessLogs = items.Where(i => i.AccessLog != null).Select(i => i.AccessLog!).ToList();
            var dataAudits = items.SelectMany(i => i.DataAudits).ToList();
            var results = new List<AuditRecordResult>(accessLogs.Count + dataAudits.Count);
            if (results.Capacity == 0)
            {
                return new AuditBatchResult(results);
            }

            IReadOnlyCollection<Guid> storedAccess, storedData;
            try
            {
                storedAccess = accessLogs.Count > 0
                    ? await _store.GetExistingAccessLogIdsAsync(accessLogs.Select(a => a.Id).ToList(), cancellationToken)
                    : Array.Empty<Guid>();
                storedData = dataAudits.Count > 0
                    ? await _store.GetExistingDataAuditIdsAsync(dataAudits.Select(d => d.Id).ToList(), cancellationToken)
                    : Array.Empty<Guid>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing is known about what is stored: the database is the problem, not the records.
                var reason = Reason(ex);
                results.AddRange(accessLogs.Select(a => new AuditRecordResult(a.Id, AuditRecordKind.Access,
                    AuditRecordOutcome.Retryable, reason)));
                results.AddRange(dataAudits.Select(d => new AuditRecordResult(d.Id, AuditRecordKind.Data,
                    AuditRecordOutcome.Retryable, reason)));
                return new AuditBatchResult(results);
            }

            var pending = new List<(AuditRecordKind Kind, Guid Id, object Record)>();
            Select(accessLogs, storedAccess, AuditRecordKind.Access, a => a.Id, results, pending);
            Select(dataAudits, storedData, AuditRecordKind.Data, d => d.Id, results, pending);
            if (_processors.Length > 0)
            {
                pending = await ProcessAsync(pending, results, cancellationToken);
            }
            if (pending.Count == 0)
            {
                return new AuditBatchResult(results);
            }

            try
            {
                foreach (var record in pending)
                {
                    Add(record.Record);
                }
                await _store.SaveChangesAsync(cancellationToken);
                results.AddRange(pending.Select(p => new AuditRecordResult(p.Id, p.Kind, AuditRecordOutcome.Stored)));
                return new AuditBatchResult(results);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _store.ClearTracked();
                if (_classifier.Classify(ex) is AuditWriteError.Transient or AuditWriteError.Database)
                {
                    var reason = Reason(ex);
                    results.AddRange(pending.Select(p =>
                        new AuditRecordResult(p.Id, p.Kind, AuditRecordOutcome.Retryable, reason)));
                    return new AuditBatchResult(results);
                }
            }

            // A key conflict (a concurrent writer of the same batch) or a record the database refuses: find which.
            var oneByOne = new List<AuditRecordResult>(pending.Count);
            for (var i = 0; i < pending.Count; i++)
            {
                var record = pending[i];
                try
                {
                    Add(record.Record);
                    await _store.SaveChangesAsync(cancellationToken);
                    oneByOne.Add(new AuditRecordResult(record.Id, record.Kind, AuditRecordOutcome.Stored));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _store.ClearTracked();
                    var error = _classifier.Classify(ex);
                    if (error is AuditWriteError.Transient or AuditWriteError.Database)
                    {
                        // The database, not this record: the rest would fail the same way. All wait for a retry.
                        var reason = Reason(ex);
                        oneByOne.AddRange(pending.Skip(i).Select(p =>
                            new AuditRecordResult(p.Id, p.Kind, AuditRecordOutcome.Retryable, reason)));
                        break;
                    }
                    oneByOne.Add(error == AuditWriteError.UniqueViolation
                        ? new AuditRecordResult(record.Id, record.Kind, AuditRecordOutcome.Duplicate)
                        : new AuditRecordResult(record.Id, record.Kind, AuditRecordOutcome.Rejected, Reason(ex)));
                }
            }
            // Every record refused for the same reason: most likely the database again, in a way not recognized
            // above. Refusing would lose them all for good; a retry costs a few attempts if they really are invalid.
            if (oneByOne.Count > 1 && oneByOne.All(r => r.Outcome == AuditRecordOutcome.Rejected)
                && oneByOne.Select(r => r.Reason).Distinct().Count() == 1)
            {
                oneByOne = oneByOne.Select(r => r with { Outcome = AuditRecordOutcome.Retryable }).ToList();
            }
            results.AddRange(oneByOne);
            return new AuditBatchResult(results);
        }

        private static void Select<T>(List<T> records, IReadOnlyCollection<Guid> stored, AuditRecordKind kind,
            Func<T, Guid> id, List<AuditRecordResult> results, List<(AuditRecordKind, Guid, object)> pending)
            where T : class
        {
            var known = new HashSet<Guid>(stored);
            foreach (var record in records)
            {
                // A record already stored, or sent twice in the same batch, is stored once.
                if (!known.Add(id(record)))
                {
                    results.Add(new AuditRecordResult(id(record), kind, AuditRecordOutcome.Duplicate));
                }
                else
                {
                    pending.Add((kind, id(record), record));
                }
            }
        }

        /// <summary>
        /// Runs the processors on each new record; a record one of them rejects, or that makes one throw, is rejected
        /// alone and left out of the save.
        /// </summary>
        private async Task<List<(AuditRecordKind Kind, Guid Id, object Record)>> ProcessAsync(
            List<(AuditRecordKind Kind, Guid Id, object Record)> pending, List<AuditRecordResult> results,
            CancellationToken cancellationToken)
        {
            var kept = new List<(AuditRecordKind, Guid, object)>(pending.Count);
            foreach (var record in pending)
            {
                var envelope = record.Record is AccessLog access
                    ? new AuditRecordEnvelope(access)
                    : new AuditRecordEnvelope((DataAudit)record.Record);
                string? rejection = null;
                foreach (var processor in _processors)
                {
                    try
                    {
                        var outcome = await processor.ProcessAsync(envelope, cancellationToken);
                        rejection = outcome.Rejection;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        rejection = $"{processor.GetType().Name} failed: {Reason(ex)}";
                    }
                    if (rejection != null)
                    {
                        break;
                    }
                }
                if (rejection != null)
                {
                    results.Add(new AuditRecordResult(record.Id, record.Kind, AuditRecordOutcome.Rejected, rejection));
                }
                else
                {
                    kept.Add(record);
                }
            }
            return kept;
        }

        private void Add(object record)
        {
            switch (record)
            {
                case AccessLog accessLog:
                    _store.Add(accessLog);
                    break;
                case DataAudit dataAudit:
                    _store.Add(dataAudit);
                    break;
            }
        }

        private static string Reason(Exception ex) => ex.GetBaseException().Message;
    }

    /// <summary>
    /// Used when the store registers no classifier: transient provider errors and timeouts are retryable, anything
    /// else is unknown. Key conflicts can't be recognized without the provider, so they count as rejected.
    /// </summary>
    internal sealed class DefaultAuditWriteErrorClassifier : IAuditWriteErrorClassifier
    {
        public static readonly DefaultAuditWriteErrorClassifier Instance = new();

        public AuditWriteError Classify(Exception exception)
        {
            for (var ex = exception; ex != null; ex = ex.InnerException)
            {
                if (ex is TimeoutException || ex is DbException { IsTransient: true })
                {
                    return AuditWriteError.Transient;
                }
            }
            return AuditWriteError.Other;
        }
    }
}
