using Juice.Domain;
using Juice.EF;
using Juice.EF.Extensions;
using Juice.MediatR;
using Juice.MediatR.Behaviors;
using Juice.Messaging;
using Juice.Messaging.Outbox;
using Juice.Messaging.Outbox.EF;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Juice.Audit.Tests.Host.Sample
{
    /// <summary>
    /// The host's business data: orders in an in-memory SQLite database with an outbox, changed under
    /// <c>TransactionBehavior</c>, so their audit goes through the outbox (US1). Each host gets its own database.
    /// </summary>
    public class SampleOrderContext : DbContextBase, IOutboxContext
    {
        private readonly SampleSwitches _switches;

        public SampleOrderContext(IServiceProvider serviceProvider, DbContextOptions<SampleOrderContext> options)
            : base(options)
        {
            ConfigureServices(serviceProvider);
            _switches = serviceProvider.GetService<SampleSwitches>() ?? new SampleSwitches();
        }

        /// <summary>Fails the commit when asked: the change is saved and its audit events raised, then rolled back.</summary>
        public override Task<bool> CommitTransactionAsync(Guid transactionId, CancellationToken token = default)
            => _switches.FailCommit
                ? throw new InvalidOperationException("order refused")
                : base.CommitTransactionAsync(transactionId, token);

        public DbSet<Order> Orders { get; set; } = default!;
        public DbSet<OutboxEvent> Outbox { get; set; } = default!;
        public DbSet<OutboxDelivery> OutboxDeliveries { get; set; } = default!;

        /// <summary>
        /// SQLite can't order or compare <see cref="DateTimeOffset"/>, which the outbox delivery queries do: store it
        /// as a sortable number. A real app's database doesn't need this.
        /// </summary>
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            base.ConfigureConventions(configurationBuilder);
            configurationBuilder.Properties<DateTimeOffset>()
                .HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
        }

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasKey(o => o.Id);
                entity.IsAuditable();
            });
            this.ConfigureOutbox(modelBuilder);
        }
    }

    public sealed class Order
    {
        private Order() { }
        public Order(string number) => (Id, Number) = (Guid.NewGuid(), number);
        public Guid Id { get; private set; }
        public string Number { get; private set; } = "";
    }

    /// <summary>Per request: makes the commit of its transaction fail.</summary>
    public sealed class SampleSwitches
    {
        public bool FailCommit { get; set; }
    }

    /// <summary>Places an order; <see cref="Fail"/> makes the commit fail, so the change rolls back.</summary>
    public sealed record PlaceOrder(string Number, bool Fail = false) : MessageBase, IRequest<Guid>;

    public sealed class PlaceOrderHandler : IRequestHandler<PlaceOrder, Guid>
    {
        private readonly SampleOrderContext _context;
        private readonly SampleSwitches _switches;

        public PlaceOrderHandler(SampleOrderContext context, SampleSwitches switches)
        {
            _context = context;
            _switches = switches;
        }

        public ValueTask<Guid> Handle(PlaceOrder request, CancellationToken cancellationToken)
        {
            _switches.FailCommit = request.Fail;
            var order = new Order(request.Number);
            _context.Orders.Add(order);
            return ValueTask.FromResult(order.Id);
        }
    }

    public sealed class SampleTransactionBehavior<TRequest, TResponse>
        : TransactionBehavior<TRequest, TResponse, SampleOrderContext>
        where TRequest : IRequest<TResponse>
    {
        public SampleTransactionBehavior(SampleOrderContext dbContext, IOutboxService<SampleOrderContext> outboxService,
            IMediator mediator, ILogger<SampleTransactionBehavior<TRequest, TResponse>> logger)
            : base(dbContext, outboxService, mediator, logger)
        {
        }
    }

    public static class SampleOrdersExtensions
    {
        /// <summary>The sample context on its own in-memory SQLite database, kept open for the host's lifetime.</summary>
        public static IServiceCollection AddSampleOrders(this IServiceCollection services)
        {
            var connection = new SqliteConnection($"Data Source=orders-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            connection.Open();
            services.AddSingleton(connection);
            services.AddScoped<SampleSwitches>();
            services.AddScoped(sp => new SampleOrderContext(sp,
                new DbContextOptionsBuilder<SampleOrderContext>().UseSqlite(sp.GetRequiredService<SqliteConnection>()).Options));
            services.AddScoped(typeof(IPipelineBehavior<PlaceOrder, Guid>), typeof(SampleTransactionBehavior<PlaceOrder, Guid>));
            services.AddScoped<IRequestHandler<PlaceOrder, Guid>, PlaceOrderHandler>();
            return services;
        }

        public static async Task CreateSampleOrdersAsync(this IServiceProvider services)
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SampleOrderContext>().Database.EnsureCreatedAsync();
        }

        /// <summary><c>POST /orders?number=…&amp;fail=…</c>: places an order through the mediator.</summary>
        public static IEndpointRouteBuilder MapSampleOrders(this IEndpointRouteBuilder app)
        {
            app.MapPost("/orders", async (string number, bool? fail, IMediator mediator) =>
            {
                try
                {
                    return Results.Ok(await mediator.Send(new PlaceOrder(number, fail ?? false)));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Problem(ex.Message, statusCode: 409);
                }
            });
            return app;
        }
    }
}
