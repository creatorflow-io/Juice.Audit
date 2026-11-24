using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Juice.Audit.Commands;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.EF;
using Juice.EF.Extensions;
using Juice.MediatR;
using Juice.XUnit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Audit.Tests
{
    /// <summary>
    /// The collector's batch write on a real database (US4): a replay or a concurrent writer of the same batch leaves
    /// one copy and no error, and a batch goes in a handful of commands, not one per record (SC-003).
    /// Requires <c>ConnectionStrings__SqlServerConnection</c> / <c>ConnectionStrings__PostgreConnection</c>.
    /// </summary>
    public sealed class IdempotentWriteTests
    {
        private static ServiceProvider Build(string provider)
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediatR();
            services.AddAuditCommandHandlers()
                .AddAuditDbContext(configuration, options => options.DatabaseProvider = provider)
                .AddEFAuditRepos();
            return services.BuildServiceProvider();
        }

        private static async Task MigrateAsync(IServiceProvider sp)
        {
            using var scope = sp.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AuditDbContext>().MigrateAsync();
        }

        private static List<AuditBatchItem> Batch(int count, string traceId)
            => Enumerable.Range(0, count).Select(i =>
            {
                var access = new AccessLog("idempotent_test", "xunit");
                access.SetRequestInfo(new RequestInfo("POST", "/orders/" + i, null, null, null, "https", null, traceId,
                    "localhost"));
                var data = new DataAudit("xunit", DateTimeOffset.UtcNow, "Inserted", "db", "dbo", "Orders",
                    "{\"Id\":" + i + "}", "{}", traceId);
                return new AuditBatchItem(access, new[] { data });
            }).ToList();

        private static async Task<AuditBatchResult> SaveAsync(IServiceProvider sp, IReadOnlyList<AuditBatchItem> items)
        {
            using var scope = sp.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<IRequestHandler<SaveAuditBatchCommand, AuditBatchResult>>()
                .Handle(new SaveAuditBatchCommand(items), default);
        }

        private static async Task<(int Access, int Data)> CountAsync(IServiceProvider sp, string traceId)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            return (await db.AccessLogs.CountAsync(a => a.Request!.TraceId == traceId),
                await db.AuditEntries.CountAsync(d => d.TraceId == traceId));
        }

        [IgnoreOnCITheory(DisplayName = "A replayed batch leaves one copy of each record")]
        [InlineData("SqlServer")]
        [InlineData("PostgreSQL")]
        public async Task A_replay_leaves_one_copy_Async(string provider)
        {
            using var sp = Build(provider);
            await MigrateAsync(sp);
            var traceId = Guid.NewGuid().ToString("N");
            var batch = Batch(5, traceId);

            var first = await SaveAsync(sp, batch);
            var replay = await SaveAsync(sp, batch);

            first.Results.Should().HaveCount(10).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Stored);
            replay.Results.Should().HaveCount(10).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Duplicate);
            (await CountAsync(sp, traceId)).Should().Be((5, 5));
        }

        [IgnoreOnCITheory(DisplayName = "Two writers of the same batch at once leave one copy, with no error")]
        [InlineData("SqlServer")]
        [InlineData("PostgreSQL")]
        public async Task Concurrent_writers_leave_one_copy_Async(string provider)
        {
            using var sp = Build(provider);
            await MigrateAsync(sp);
            for (var round = 0; round < 5; round++)
            {
                var traceId = Guid.NewGuid().ToString("N");
                var batch = Batch(20, traceId);
                var gate = new TaskCompletionSource();

                var writers = Enumerable.Range(0, 2).Select(async _ =>
                {
                    await gate.Task;
                    return await SaveAsync(sp, batch);
                }).ToList();
                gate.SetResult();
                var results = await Task.WhenAll(writers);

                foreach (var result in results)
                {
                    result.AllStored.Should().BeTrue("a key conflict with the other writer is a duplicate, not an error: "
                        + result.ToOperationResult());
                }
                results.SelectMany(r => r.Results).Count(r => r.Outcome == AuditRecordOutcome.Stored).Should().Be(40,
                    "each record is stored by exactly one writer");
                (await CountAsync(sp, traceId)).Should().Be((20, 20));
            }
        }

        [IgnoreOnCITheory(DisplayName = "SC-003: a 512-record batch is written in a few commands per table")]
        [InlineData("SqlServer")]
        [InlineData("PostgreSQL")]
        public async Task A_batch_is_written_in_few_commands_Async(string provider)
        {
            using var sp = Build(provider);
            await MigrateAsync(sp);
            var traceId = Guid.NewGuid().ToString("N");
            var batch = Batch(512, traceId);
            using var commands = new CommandCounter(traceId);
            // EF caches whether anyone listens for 1 s (DbContextOptionsBuilder.ConfigureLoggingCacheTime): a listener that
            // joins later is missed until the cache expires, e.g. right after another test wrote.
            await Task.Delay(TimeSpan.FromSeconds(1.2));

            var result = await SaveAsync(sp, batch);

            result.AllStored.Should().BeTrue(result.ToOperationResult().ToString());
            var access = commands.Inserts(nameof(AccessLog));
            var data = commands.Inserts(nameof(DataAudit));
            access.Commands.Should().BePositive("the counter sees the inserts");
            data.Commands.Should().BePositive();
            // At most ⌈512 / rows per command⌉ + 1, where a command holds MaxBatchSize rows (1000), or fewer when the
            // provider's parameter limit (2100 on SQL Server) is reached first.
            Bound(provider, access).Should().BeGreaterThanOrEqualTo(access.Commands, $"{provider}: {nameof(AccessLog)}");
            Bound(provider, data).Should().BeGreaterThanOrEqualTo(data.Commands, $"{provider}: {nameof(DataAudit)}");
            (await CountAsync(sp, traceId)).Should().Be((512, 512));
        }

        private static int Bound(string provider, (int Commands, int ParametersPerRow) inserts)
        {
            var rowsPerCommand = provider == "SqlServer"
                ? Math.Min(1000, 2098 / Math.Max(1, inserts.ParametersPerRow))
                : 1000;
            return (int)Math.Ceiling(512.0 / rowsPerCommand) + 1;
        }

        /// <summary>
        /// Counts the INSERT commands EF sends, per table, through its diagnostic events. The events are process-wide,
        /// so only commands carrying <c>traceId</c> are this test's: other test classes write in parallel.
        /// </summary>
        private sealed class CommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>,
            IDisposable
        {
            private const int Rows = 512;
            private readonly string _traceId;
            private readonly ConcurrentQueue<(string Text, int Parameters)> _commands = new();
            private readonly List<IDisposable> _subscriptions = new();

            public CommandCounter(string traceId)
            {
                _traceId = traceId;
                _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
            }

            /// <summary>The INSERTs into <paramref name="table"/>, and the parameters each of its rows takes.</summary>
            public (int Commands, int ParametersPerRow) Inserts(string table)
            {
                var inserts = _commands.Where(c => c.Text.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
                    && (c.Text.Contains($"[{table}]") || c.Text.Contains($"\"{table}\""))).ToList();
                // Every row takes the same parameters: the batch's total over its rows.
                var perRow = (int)Math.Ceiling(inserts.Sum(c => c.Parameters) / (double)Rows);
                return (inserts.Count, perRow);
            }

            public void OnNext(DiagnosticListener listener)
            {
                if (listener.Name == DbLoggerCategory.Name)
                {
                    lock (_subscriptions)
                    {
                        _subscriptions.Add(listener.Subscribe(this));
                    }
                }
            }

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Key == RelationalEventId.CommandExecuting.Name && value.Value is CommandEventData data
                    && data.Command.Parameters.Cast<System.Data.Common.DbParameter>().Any(p => Equals(p.Value, _traceId)))
                {
                    _commands.Enqueue((data.Command.CommandText, data.Command.Parameters.Count));
                }
            }

            public void OnCompleted() { }

            public void OnError(Exception error) { }

            public void Dispose()
            {
                lock (_subscriptions)
                {
                    _subscriptions.ForEach(s => s.Dispose());
                }
            }
        }
    }
}
