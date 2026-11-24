using System.Collections.Concurrent;
using System.Reflection;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.Export;
using Juice.Messaging;
using Juice.Messaging.Extensions;
using Juice.Messaging.Policies;
using Juice.MultiTenant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Juice.Audit.Messaging.Outbox
{
    /// <summary>
    /// Puts a transaction's data changes into its outbox, in one <see cref="DataChangesAudited"/> event per transaction
    /// (or a few, at the size limits). The event is added once and filled as changes arrive: the outbox serializes its
    /// events when <c>TransactionBehavior</c> saves them, after every dispatch round, so later changes are included.
    /// <para>A change goes best effort, counted by reason, when the outbox can't carry it: no transaction, no binding
    /// owns the transaction, the policy routes the event nowhere (the outbox would skip it silently), the change
    /// belongs to another tenant than the outbox stamps, or anything fails. Never throws.</para>
    /// </summary>
    internal sealed class OutboxDataChangeSink : IDataChangeSink
    {
        private const string LocalChannel = "local-channel";

        // Once per process, so a misconfiguration is visible without flooding the log.
        private static readonly ConcurrentDictionary<string, bool> s_noRouteWarned = new();
        private static int s_noBindingWarned;

        private readonly IEnumerable<IAuditOutboxBinding> _bindings;
        private readonly IMessagePublishingPolicy? _policy;
        private readonly ITenantAccessor? _tenantAccessor;
        private readonly IMessageSerializer _serializer;
        private readonly IAuditContextAccessor _scope;
        private readonly AuditOutboxOptions _options;
        private readonly AuditExportMetrics _metrics;
        private readonly ILogger _logger;
        private readonly string? _app;

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Dictionary<Guid, OpenEvent> _open = new();
        private readonly Dictionary<string, bool> _routes = new();

        public OutboxDataChangeSink(IEnumerable<IAuditOutboxBinding> bindings, IAuditContextAccessor scope,
            IOptions<AuditOutboxOptions> options, AuditExportMetrics metrics, IMessageSerializer serializer,
            ILogger<OutboxDataChangeSink> logger, IMessagePublishingPolicy? policy = null,
            ITenantAccessor? tenantAccessor = null)
        {
            _serializer = serializer;
            _bindings = bindings;
            _scope = scope;
            _options = options.Value;
            _metrics = metrics;
            _logger = logger;
            _policy = policy;
            _tenantAccessor = tenantAccessor;
            _app = _options.AppName ?? Assembly.GetEntryAssembly()?.GetName().Name;
        }

        public async ValueTask<bool> TryAppendAsync(Guid? transactionId, DataAudit entry,
            CancellationToken cancellationToken)
        {
            try
            {
                if (_options.RequireScope && !_scope.IsInitialized)
                {
                    return false;
                }
                if (transactionId is not { } id)
                {
                    return BestEffort(AuditBestEffortReason.NoTransaction);
                }
                var binding = _bindings.FirstOrDefault(b => b.Runs(id));
                if (binding == null)
                {
                    if (Interlocked.Exchange(ref s_noBindingWarned, 1) == 0)
                    {
                        _logger.LogWarning("A data change of transaction {TransactionId} is audited best effort: no " +
                            "AddAuditOutbox<TContext>() is registered for the context that runs it.", id);
                    }
                    return BestEffort(AuditBestEffortReason.NoBinding);
                }
                if (!binding.IsManaged)
                {
                    // The app's own BeginTransaction: only TransactionBehavior saves the outbox.
                    return BestEffort(AuditBestEffortReason.Unmanaged);
                }
                var tenant = _tenantAccessor?.Tenant;
                if (!string.IsNullOrEmpty(entry.TenantId) && entry.TenantId != tenant?.Id)
                {
                    return BestEffort(AuditBestEffortReason.TenantMismatch);
                }
                if (!await HasRouteAsync(tenant?.Identifier, tenant?.Tier))
                {
                    return BestEffort(AuditBestEffortReason.NoRoute);
                }

                var (change, size) = ToEntry(entry, Envelope(id, tenant?.Id, int.MaxValue));
                await _lock.WaitAsync(cancellationToken);
                try
                {
                    if (!_open.TryGetValue(id, out var open) || IsFull(open, size))
                    {
                        var evt = new DataChangesAudited
                        {
                            App = _app,
                            TransactionId = id,
                            TenantId = tenant?.Id,
                            Part = (open?.Event.Part ?? 0) + 1
                        };
                        // An event once added stays in the outbox until it is saved, though its transaction rolls
                        // back: the next save of the scope (another command, or EF retrying the transaction under a
                        // new id) would store changes that never committed. It is written empty then.
                        evt.IsInTransaction = () => binding.Runs(id);
                        await binding.Outbox.AddEventAsync(evt);
                        _open[id] = open = new OpenEvent(evt) { Bytes = Bytes(evt) };
                    }
                    open.Event.Entries.Add(change);
                    open.Bytes += size;
                }
                finally
                {
                    _lock.Release();
                }
                _metrics.Outboxed();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A data change of transaction {TransactionId} is audited best effort: it could " +
                    "not be added to the outbox.", transactionId);
                return BestEffort(AuditBestEffortReason.Error);
            }
        }

        private bool BestEffort(AuditBestEffortReason reason)
        {
            _metrics.BestEffort(reason);
            return false;
        }

        private bool IsFull(OpenEvent open, int size)
            => open.Event.Entries.Count >= _options.MaxEntriesPerEvent
                || (open.Event.Entries.Count > 0 && open.Bytes + size > _options.MaxEventBytes);

        /// <summary>
        /// The outbox saves an event only on a route other than the in-process channel; checked here, once per tenant
        /// in the scope, so a change it would skip goes best effort instead of nowhere.
        /// </summary>
        private async ValueTask<bool> HasRouteAsync(string? tenantIdentifier, string? tenantTier)
        {
            var key = tenantIdentifier ?? "";
            if (_routes.TryGetValue(key, out var known))
            {
                return known;
            }
            var routes = _policy == null
                ? Array.Empty<PublishRoute>()
                : await _policy.ResolveAsync(new PolicyResolveContext
                {
                    Domain = typeof(DataChangesAudited).GetDomainName(),
                    EventType = nameof(DataChangesAudited),
                    TenantIdentifier = tenantIdentifier,
                    TenantTier = tenantTier
                });
            var found = routes.Any(r => r.PublisherKey != LocalChannel);
            _routes[key] = found;
            if (!found && s_noRouteWarned.TryAdd(key, true))
            {
                _logger.LogWarning("Data changes are audited best effort for tenant {Tenant}: the publishing policy " +
                    "routes domain {Domain} nowhere.", tenantIdentifier ?? "(root)", typeof(DataChangesAudited).GetDomainName());
            }
            return found;
        }

        /// <summary>
        /// The entry of <paramref name="entry"/>, and its <see cref="Size"/>. A change that doesn't fit in an event of its own, next to
        /// <paramref name="envelope"/> bytes of event, keeps what fits, as a string, so the stored value stays valid
        /// JSON: <c>{"$truncated":true,"$originalSize":n,"$partial":"…"}</c>.
        /// </summary>
        private (DataChangeEntry Entry, int Size) ToEntry(DataAudit entry, int envelope)
        {
            var change = new DataChangeEntry
            {
                Id = entry.Id,
                User = entry.User,
                DateTime = entry.DateTime,
                Action = entry.Action,
                Db = entry.Db,
                Schema = entry.Schema,
                Tbl = entry.Tbl,
                Kvps = entry.Kvps,
                Changes = entry.Changes,
                TraceId = entry.TraceId,
                TenantId = entry.TenantId
            };
            var size = 0;
            var changes = AuditTruncation.TruncateJson(entry.Changes, _options.MaxEventBytes - envelope, candidate =>
            {
                change.Changes = candidate;
                return size = Size(change);
            });
            change.Changes = changes;
            // Measured last on the whole change when it fits; a cut one, on whichever cut was tried last.
            return (change, ReferenceEquals(changes, entry.Changes) ? size : Size(change));
        }

        /// <summary>
        /// The bytes an entry adds to its event, as the outbox stores it: serialized, its JSON strings escaped again,
        /// with the serializer's type names, and the comma before it.
        /// </summary>
        private int Size(DataChangeEntry change) => _serializer.SerializeToUtf8Bytes(change).Length + 1;

        /// <summary>The bytes of an event, as the outbox stores it.</summary>
        private int Bytes(DataChangesAudited evt) => _serializer.SerializeToUtf8Bytes(evt).Length;

        /// <summary>
        /// The bytes of an event of the transaction with no entries yet, for the room one entry has; part
        /// <see cref="int.MaxValue"/> is the longest a part number can be written.
        /// </summary>
        private int Envelope(Guid transactionId, string? tenantId, int part)
            => Bytes(new DataChangesAudited { App = _app, TransactionId = transactionId, TenantId = tenantId, Part = part });

        private sealed class OpenEvent
        {
            public OpenEvent(DataChangesAudited evt) => Event = evt;
            public DataChangesAudited Event { get; }
            public int Bytes { get; set; }
        }
    }
}
