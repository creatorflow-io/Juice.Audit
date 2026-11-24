using System.Diagnostics.Metrics;
using Juice.Audit.Api.Scopes;
using Juice.Audit.Commands;
using Juice.Audit.Domain;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.Export;
using Juice.Audit.Messaging;
using Juice.Audit.Messaging.Outbox;
using Juice.Domain.Events;
using Juice.EF;
using Juice.EF.Extensions;
using Juice.MediatR;
using Juice.MediatR.Behaviors;
using Juice.Messaging;
using Juice.Messaging.Idempotency;
using Juice.Messaging.Outbox;
using Juice.Messaging.Outbox.EF;
using Juice.Messaging.Policies;
using Juice.MultiTenant;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// An app whose business context, <see cref="SampleOrderContext"/>, runs under a real
    /// <see cref="TransactionBehavior{TRequest, TResponse, TContext}"/> on SQLite with an outbox, audited with
    /// <c>AddAuditOutbox&lt;SampleOrderContext&gt;()</c>. The access records go to a recorder; the database outlives
    /// the provider, as a real one outlives a process.
    /// </summary>
    internal sealed class OutboxFixture : IDisposable
    {
        public SqliteConnection Connection { get; } = new("DataSource=:memory:");

        public OutboxFixture()
        {
            Connection.Open();
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }

        public void Dispose() => Connection.Dispose();

        public SampleOrderContext CreateContext(IServiceProvider? services = null)
            => new(services ?? new ServiceCollection().AddLogging().BuildServiceProvider(),
                new DbContextOptionsBuilder<SampleOrderContext>().UseSqlite(Connection).Options);

        /// <param name="routes">The routes the publishing policy gives <c>juice.audit</c>.</param>
        /// <param name="sink">false: the app hasn't called AddAuditOutbox, as before 10.7.</param>
        /// <param name="mediatorAudit">false: commands sent outside a scope run with no audit scope at all.</param>
        public ServiceProvider BuildApp(PublishRoute[]? routes = null, bool sink = true, string? tenant = null,
            Action<AuditOutboxOptions>? configure = null, bool throwingPolicy = false, bool mediatorAudit = true)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SampleSwitches>();
            services.AddScoped(sp => CreateContext(sp));
            services.AddMediatR();
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(SampleTransactionBehavior<,>));
            services.AddScoped<IRequestHandler<PlaceOrders, int>, PlaceOrdersHandler>();
            services.AddScoped<INotificationHandler<AuditEvent<Order>>, AddLineOnFirstOrder>();
            services.AddMessaging().AddOutbox();
            services.AddSingleton<IMessagePublishingPolicy>(throwingPolicy
                ? new ThrowingPolicy()
                : new FixedPolicy(routes ?? new[] { new PublishRoute("local", "juice.audit") }));
            if (tenant != null)
            {
                services.AddSingleton<ITenantAccessor>(new FixedTenant(tenant));
            }

            services.AddAuditContextAccessor().AddDataEventHandler();
            if (mediatorAudit)
            {
                services.AddMediatorAudit();
            }
            else
            {
                services.AddAuditScopeRunner();
            }
            services.AddSingleton<AccessRecords>();
            services.AddScoped<IAuditService, RecordingAuditService>();
            if (sink)
            {
                services.AddAuditOutbox<SampleOrderContext>(o =>
                {
                    o.AppName = "orders-api";
                    configure?.Invoke(o);
                });
            }
            return services.BuildServiceProvider();
        }

        /// <summary>The collector: the data change consumer storing into <paramref name="store"/>.</summary>
        public static ServiceProvider BuildCollector(MemoryAuditStore store)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediatR();
            services.AddMessaging(m => m.AddLocalConsumer(c => c.SubscribeAuditDataChanges()));
            services.AddScoped<IIdempotencyService, AlwaysNew>();
            services.AddAuditCommandHandlers();
            services.AddSingleton<IAuditBatchStore>(store);
            // As a collector registers it: its own consumption isn't audited (MessageAuditBehavior passes it).
            services.AddAuditContextAccessor().AddDataEventHandler();
            services.AddMessageAudit();
            services.AddSingleton<AccessRecords>();
            services.AddScoped<IAuditService, RecordingAuditService>();
            return services.BuildServiceProvider();
        }

        /// <summary>The events in the outbox table, as the delivery processor would read them.</summary>
        /// <summary>The bytes of each audit event as the outbox stores and publishes it.</summary>
        public List<int> OutboxPayloadSizes()
        {
            using var context = CreateContext();
            return context.Outbox.AsNoTracking().ToList()
                .Where(e => e.EventTypeName == nameof(DataChangesAudited))
                .Select(e => e.PayloadBytes.Length)
                .ToList();
        }

        public List<DataChangesAudited> OutboxEvents()
        {
            using var context = CreateContext();
            using var services = new ServiceCollection().AddLogging().AddMessaging().Services.BuildServiceProvider();
            var serializer = services.GetRequiredService<IMessageSerializer>();
            return context.Outbox.AsNoTracking().ToList()
                .Where(e => e.EventTypeName == nameof(DataChangesAudited))
                .Select(e => serializer.DeserializeFromUtf8Bytes<DataChangesAudited>(e.PayloadBytes,
                    typeof(DataChangesAudited))!)
                .ToList();
        }

        /// <summary>Runs <paramref name="send"/> in an audit scope, as a request would.</summary>
        public static async Task RunInScopeAsync(IServiceProvider provider, Func<IMediator, Task> send,
            string? tenant = null)
        {
            using var scope = provider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await scope.ServiceProvider.GetRequiredService<AuditScopeRunner>().RunAsync(
                new AuditScopeRequest("orders", AuditScopeMethods.Request, "orders", tenant),
                async () =>
                {
                    await send(mediator);
                    return true;
                },
                _ => AuditScopeOutcome.Ok);
        }

        /// <summary>Runs <paramref name="work"/> with the scope's services, in an audit scope.</summary>
        public static async Task RunWithServicesInScopeAsync(IServiceProvider provider, Func<IServiceProvider, Task> work)
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AuditScopeRunner>().RunAsync(
                new AuditScopeRequest("orders", AuditScopeMethods.Request, "orders", null),
                async () =>
                {
                    await work(scope.ServiceProvider);
                    return true;
                },
                _ => AuditScopeOutcome.Ok);
        }

        public static List<(AccessLog AccessLog, DataAudit[] DataAudits)> Records(IServiceProvider provider)
            => provider.GetRequiredService<AccessRecords>().Records;

        /// <summary>Measurements of the app's <c>Juice.Audit</c> meter, by instrument and tags.</summary>
        public static MeterRecorder ListenTo(IServiceProvider provider)
            => new(provider.GetRequiredService<AuditExportMetrics>().Meter);
    }

    public sealed class Order
    {
        private Order() { }
        public Order(string number) => (Id, Number) = (Guid.NewGuid(), number);
        public Guid Id { get; private set; }
        public string Number { get; private set; } = "";
    }

    public sealed class Line
    {
        private Line() { }
        public Line(Guid orderId) => (Id, OrderId) = (Guid.NewGuid(), orderId);
        public Guid Id { get; private set; }
        public Guid OrderId { get; private set; }
    }

    /// <summary>Per app provider, so test classes running in parallel don't share them.</summary>
    public sealed class SampleSwitches
    {
        /// <summary>The commit fails: the transaction rolls back after its events are dispatched.</summary>
        public bool FailCommit { get; set; }

        /// <summary>The next order's audit event makes a handler add a line, in a second dispatch round.</summary>
        public bool AddLine { get; set; }

        /// <summary>
        /// Saving the line fails, in the second dispatch round: the transaction rolls back after its first round's
        /// changes reached the outbox, before the outbox is saved. (A failing audit event handler wouldn't do: core
        /// logs and swallows those.)
        /// </summary>
        public bool FailOnLine { get; set; }
    }

    public class SampleOrderContext : DbContextBase, IOutboxContext
    {
        private readonly SampleSwitches _switches;

        public SampleOrderContext(IServiceProvider serviceProvider, DbContextOptions<SampleOrderContext> options)
            : base(options)
        {
            ConfigureServices(serviceProvider);
            _switches = serviceProvider.GetService<SampleSwitches>() ?? new SampleSwitches();
        }

        public DbSet<Order> Orders { get; set; } = default!;
        public DbSet<Line> Lines { get; set; } = default!;
        public DbSet<OutboxEvent> Outbox { get; set; } = default!;
        public DbSet<OutboxDelivery> OutboxDeliveries { get; set; } = default!;

        public override Task<bool> CommitTransactionAsync(Guid transactionId, CancellationToken token = default)
            => _switches.FailCommit
                ? throw new InvalidOperationException("commit failed")
                : base.CommitTransactionAsync(transactionId, token);

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
            => _switches.FailOnLine && ChangeTracker.Entries<Line>().Any(e => e.State == EntityState.Added)
                ? throw new InvalidOperationException("line refused")
                : base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasKey(o => o.Id);
                entity.IsAuditable();
            });
            modelBuilder.Entity<Line>(entity =>
            {
                entity.HasKey(l => l.Id);
                entity.IsAuditable();
            });
            this.ConfigureOutbox(modelBuilder);
        }
    }

    public interface ISampleCommand;

    /// <param name="WithLine">A handler adds a line in a second dispatch round.</param>
    /// <param name="Number">The number of each order, before its index: a long one makes a large change.</param>
    public sealed record PlaceOrders(int Count, bool WithLine = false, string Number = "SO-") : MessageBase, IRequest<int>,
        ISampleCommand;

    public sealed class SampleTransactionBehavior<TRequest, TResponse>
        : TransactionBehavior<TRequest, TResponse, SampleOrderContext>
        where TRequest : IRequest<TResponse>, ISampleCommand
    {
        public SampleTransactionBehavior(SampleOrderContext dbContext, IOutboxService<SampleOrderContext> outboxService,
            IMediator mediator, ILogger<SampleTransactionBehavior<TRequest, TResponse>> logger)
            : base(dbContext, outboxService, mediator, logger)
        {
        }
    }

    public sealed class PlaceOrdersHandler(SampleOrderContext context, SampleSwitches switches)
        : IRequestHandler<PlaceOrders, int>
    {
        public ValueTask<int> Handle(PlaceOrders request, CancellationToken cancellationToken)
        {
            switches.AddLine = request.WithLine;
            for (var i = 0; i < request.Count; i++)
            {
                context.Orders.Add(new Order(request.Number + i));
            }
            return ValueTask.FromResult(request.Count);
        }
    }

    /// <summary>Changes another audited entity while the first round's events are dispatched.</summary>
    public sealed class AddLineOnFirstOrder(SampleOrderContext context, SampleSwitches switches)
        : INotificationHandler<AuditEvent<Order>>
    {
        public ValueTask Handle(AuditEvent<Order> notification, CancellationToken cancellationToken)
        {
            if (switches.AddLine)
            {
                switches.AddLine = false;
                context.Lines.Add(new Line(Guid.NewGuid()));
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A collector's store, in memory: tracked records join the stored ones on save.</summary>
    public sealed class MemoryAuditStore : IAuditBatchStore
    {
        private readonly List<object> _tracked = new();
        public List<AccessLog> AccessLogs { get; } = new();
        public List<DataAudit> DataAudits { get; } = new();

        public Task<IReadOnlyCollection<Guid>> GetExistingAccessLogIdsAsync(IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<Guid>>(ids.Intersect(AccessLogs.Select(a => a.Id)).ToList());

        public Task<IReadOnlyCollection<Guid>> GetExistingDataAuditIdsAsync(IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<Guid>>(ids.Intersect(DataAudits.Select(d => d.Id)).ToList());

        public void Add(AccessLog accessLog) => _tracked.Add(accessLog);

        public void Add(DataAudit dataAudit) => _tracked.Add(dataAudit);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            AccessLogs.AddRange(_tracked.OfType<AccessLog>());
            DataAudits.AddRange(_tracked.OfType<DataAudit>());
            _tracked.Clear();
            return Task.CompletedTask;
        }

        public void ClearTracked() => _tracked.Clear();
    }

    public sealed class MeterRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measurements = new();

        public MeterRecorder(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter == meter)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var map = new Dictionary<string, object?>();
                foreach (var tag in tags)
                {
                    map[tag.Key] = tag.Value;
                }
                lock (_measurements)
                {
                    _measurements.Add((instrument.Name, value, map));
                }
            });
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                var map = new Dictionary<string, object?>();
                foreach (var tag in tags)
                {
                    map[tag.Key] = tag.Value;
                }
                lock (_measurements)
                {
                    _doubles.Add((instrument.Name, value, map));
                }
            });
            _listener.Start();
        }

        private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _doubles = new();

        /// <summary>How many measurements <paramref name="instrument"/> made with all of <paramref name="tags"/>.</summary>
        public int Count(string instrument, params (string Key, string Value)[] tags)
        {
            bool Match(string name, Dictionary<string, object?> map)
                => name == instrument && tags.All(t => map.TryGetValue(t.Key, out var v) && Equals(v, t.Value));
            lock (_measurements)
            {
                return _measurements.Count(m => Match(m.Instrument, m.Tags)) + _doubles.Count(m => Match(m.Instrument, m.Tags));
            }
        }

        /// <summary>The latest value an observable gauge reported, after asking it now.</summary>
        public long Observe(string instrument)
        {
            _listener.RecordObservableInstruments();
            lock (_measurements)
            {
                return _measurements.Last(m => m.Instrument == instrument).Value;
            }
        }

        /// <summary>The sum of <paramref name="instrument"/>'s measurements whose tags include all of <paramref name="tags"/>.</summary>
        public long Sum(string instrument, params (string Key, string Value)[] tags)
        {
            lock (_measurements)
            {
                return _measurements
                    .Where(m => m.Instrument == instrument
                        && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v, t.Value)))
                    .Sum(m => m.Value);
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    internal sealed class FixedPolicy(PublishRoute[] routes) : IMessagePublishingPolicy
    {
        public ValueTask<IReadOnlyCollection<PublishRoute>> ResolveAsync(PolicyResolveContext context)
            => ValueTask.FromResult<IReadOnlyCollection<PublishRoute>>(routes);
    }

    internal sealed class ThrowingPolicy : IMessagePublishingPolicy
    {
        public ValueTask<IReadOnlyCollection<PublishRoute>> ResolveAsync(PolicyResolveContext context)
            => throw new InvalidOperationException("policy store unavailable");
    }

    internal sealed class FixedTenant(string id) : ITenantAccessor
    {
        public ITenant? Tenant { get; } = new TenantInfo(id);

        private sealed record TenantInfo(string? Id) : ITenant
        {
            public string? Name => Id;
            public string? Identifier => Id;
            public string? OwnerUser => null;
            public string? Tier => null;
            public string? Region => null;
            public object? this[string key] => null;
            public T? GetProperty<T>(Func<T>? defaultValue = null,
                [System.Runtime.CompilerServices.CallerMemberName] string? name = null) => default;
            public void SetProperty<T>(T? value,
                [System.Runtime.CompilerServices.CallerMemberName] string? name = null) { }
        }
    }

    internal sealed class AlwaysNew : IIdempotencyService
    {
        public ValueTask<IdempotencyResult> TryBeginRequestAsync(string scope, string key, string? requestHash = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new IdempotencyResult(IdempotencyOutcome.Created));

        public ValueTask TryCompleteRequestAsync(string scope, string key, bool success, object? result = null,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    internal sealed class AccessRecords
    {
        public List<(AccessLog, DataAudit[])> Records { get; } = new();
    }

    internal sealed class RecordingAuditService(AccessRecords records) : IAuditService
    {
        public Task<IOperationResult> PersistAuditInformationAsync(AccessLog accessLog, DataAudit[] auditEntries,
            CancellationToken token)
        {
            lock (records)
            {
                records.Records.Add((accessLog, auditEntries));
            }
            return Task.FromResult<IOperationResult>(OperationResult.Success);
        }
    }
}
