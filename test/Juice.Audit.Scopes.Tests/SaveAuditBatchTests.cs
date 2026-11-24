using FluentAssertions;
using Juice.Audit.CommandHandlers;
using Juice.Audit.Commands;
using Juice.Audit.Domain;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;

namespace Juice.Audit.Scopes.Tests
{
    /// <summary>
    /// The collector's write (<see cref="SaveAuditBatchCommandHandler"/>): every record once, the batch in one save,
    /// and a result per record that tells the sender what to retry.
    /// </summary>
    public class SaveAuditBatchTests
    {
        private static AccessLog Access() => new("action", "user");

        private static DataAudit Data(string table = "Orders")
            => new("user", DateTimeOffset.UtcNow, "Modified", null, null, table, "{}", "{}", "trace");

        private static Task<AuditBatchResult> SaveAsync(FakeStore store, params AuditBatchItem[] items)
            => new SaveAuditBatchCommandHandler(store, new FakeClassifier())
                .Handle(new SaveAuditBatchCommand(items), CancellationToken.None).AsTask();

        [Fact]
        public async Task New_records_are_stored_in_one_save_Async()
        {
            var store = new FakeStore();
            var access = Access();
            var data = new[] { Data(), Data("Lines") };

            var result = await SaveAsync(store, new AuditBatchItem(access, data));

            result.AllStored.Should().BeTrue();
            result.Results.Should().HaveCount(3).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Stored);
            store.Saves.Should().Be(1);
            store.AccessLogs.Should().ContainSingle().Which.Should().BeSameAs(access);
            store.DataAudits.Should().HaveCount(2);
        }

        [Fact]
        public async Task Records_already_stored_are_duplicates_and_not_written_again_Async()
        {
            var store = new FakeStore();
            var access = Access();
            var data = Data();
            await SaveAsync(store, new AuditBatchItem(access, new[] { data }));

            var replay = await SaveAsync(store, new AuditBatchItem(access, new[] { data }));

            replay.Results.Should().OnlyContain(r => r.Outcome == AuditRecordOutcome.Duplicate);
            replay.AllStored.Should().BeTrue("a duplicate is a success for the sender");
            store.AccessLogs.Should().ContainSingle();
            store.DataAudits.Should().ContainSingle();
            store.Saves.Should().Be(1, "nothing was left to write");
        }

        [Fact]
        public async Task A_record_repeated_in_the_same_batch_is_stored_once_Async()
        {
            var store = new FakeStore();
            var access = Access();

            var result = await SaveAsync(store, new AuditBatchItem(access), new AuditBatchItem(access));

            result.Results.Select(r => r.Outcome).Should()
                .BeEquivalentTo(new[] { AuditRecordOutcome.Stored, AuditRecordOutcome.Duplicate });
            store.AccessLogs.Should().ContainSingle();
        }

        [Fact]
        public async Task An_item_of_data_changes_alone_is_stored_Async()
        {
            var store = new FakeStore();

            var result = await SaveAsync(store, new AuditBatchItem(null, new[] { Data(), Data() }));

            result.Results.Should().HaveCount(2).And.OnlyContain(r => r.Kind == AuditRecordKind.Data && r.IsStored);
            store.AccessLogs.Should().BeEmpty();
        }

        [Fact]
        public async Task A_transient_error_makes_every_record_retryable_Async()
        {
            var store = new FakeStore { FailNextSaves = 1, Failure = new TimeoutException("database down") };

            var result = await SaveAsync(store, new AuditBatchItem(Access(), new[] { Data() }));

            result.Results.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Retryable);
            result.Results[0].Reason.Should().Be("database down");
            result.AnyRetryable.Should().BeTrue();
            store.AccessLogs.Should().BeEmpty();
        }

        [Fact]
        public async Task A_failed_lookup_makes_every_record_retryable_Async()
        {
            var store = new FakeStore { FailLookup = true };

            var result = await SaveAsync(store, new AuditBatchItem(Access(), new[] { Data() }));

            result.Results.Should().OnlyContain(r => r.Outcome == AuditRecordOutcome.Retryable);
        }

        [Fact]
        public async Task A_concurrent_writer_of_the_same_ids_gives_duplicates_not_errors_Async()
        {
            // Another writer stored the access record between the lookup and the save.
            var store = new FakeStore();
            var access = Access();
            var data = Data();
            store.StoredBehind.Add(access.Id);

            var result = await SaveAsync(store, new AuditBatchItem(access, new[] { data }));

            result.Results.Single(r => r.Id == access.Id).Outcome.Should().Be(AuditRecordOutcome.Duplicate);
            result.Results.Single(r => r.Id == data.Id).Outcome.Should().Be(AuditRecordOutcome.Stored);
            store.DataAudits.Should().ContainSingle();
        }

        [Fact]
        public async Task A_record_the_database_refuses_is_rejected_alone_Async()
        {
            var store = new FakeStore();
            var bad = Data("Refused");
            var good = Data();

            var result = await SaveAsync(store, new AuditBatchItem(Access(), new[] { bad, good }));

            result.Results.Single(r => r.Id == bad.Id).Outcome.Should().Be(AuditRecordOutcome.Rejected);
            result.Results.Single(r => r.Id == bad.Id).Reason.Should().Contain("refused");
            result.Results.Where(r => r.Id != bad.Id).Should().OnlyContain(r => r.Outcome == AuditRecordOutcome.Stored);
            result.ToOperationResult().Succeeded.Should().BeFalse();
        }

        [Fact]
        public async Task SaveAuditInfoCommand_goes_through_the_same_deduplication_Async()
        {
            var store = new FakeStore();
            var handler = new SaveAuditInfoCommandHandler(null!, null!, store, new FakeClassifier());
            var access = Access();

            (await handler.Handle(new SaveAuditInfoCommand(access, Data()), default)).Succeeded.Should().BeTrue();
            (await handler.Handle(new SaveAuditInfoCommand(access), default)).Succeeded.Should().BeTrue();

            store.AccessLogs.Should().ContainSingle();
        }

        [Fact(DisplayName = "An error of the database, not of a record (a missing table), makes every record retryable at once")]
        public async Task A_database_error_makes_every_record_retryable_Async()
        {
            var store = new FakeStore { FailNextSaves = int.MaxValue, Failure = new DatabaseError() };

            var result = await SaveAsync(store, new AuditBatchItem(Access(), new[] { Data(), Data() }),
                new AuditBatchItem(Access()));

            result.Results.Should().HaveCount(4).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Retryable,
                "refusing them would lose every record sent until the migration runs");
            store.Attempts.Should().Be(1, "not retried record by record: each would fail the same way");
        }

        [Fact(DisplayName = "Every record refused for the same unrecognized reason is retryable, not lost")]
        public async Task The_same_refusal_for_every_record_is_retryable_Async()
        {
            var store = new FakeStore { FailNextSaves = int.MaxValue };

            var result = await SaveAsync(store, new AuditBatchItem(Access(), new[] { Data(), Data() }));

            result.Results.Should().HaveCount(3).And.OnlyContain(r => r.Outcome == AuditRecordOutcome.Retryable
                && r.Reason == "save failed");
        }

        [Fact(DisplayName = "A single record the database refuses is still rejected")]
        public async Task A_lone_refused_record_is_rejected_Async()
        {
            var result = await SaveAsync(new FakeStore(), new AuditBatchItem(null, new[] { Data("Refused") }));

            result.Results.Should().ContainSingle().Which.Outcome.Should().Be(AuditRecordOutcome.Rejected);
        }

        /// <summary>As a missing table: <c>relation "AccessLog" does not exist</c>.</summary>
        private sealed class DatabaseError : Exception;

        private sealed class UniqueViolation : Exception;

        private sealed class FakeClassifier : IAuditWriteErrorClassifier
        {
            public AuditWriteError Classify(Exception exception) => exception switch
            {
                UniqueViolation => AuditWriteError.UniqueViolation,
                TimeoutException => AuditWriteError.Transient,
                DatabaseError => AuditWriteError.Database,
                _ => AuditWriteError.Other
            };
        }

        /// <summary>
        /// An in-memory store. A save with a table named "Refused", or an id in <see cref="StoredBehind"/> (written by
        /// someone else after the lookup), fails the whole save, as a database would.
        /// </summary>
        private sealed class FakeStore : IAuditBatchStore
        {
            private readonly List<object> _tracked = new();
            public List<AccessLog> AccessLogs { get; } = new();
            public List<DataAudit> DataAudits { get; } = new();
            public HashSet<Guid> StoredBehind { get; } = new();
            public int Saves { get; private set; }
            public int Attempts { get; private set; }
            public int FailNextSaves { get; set; }
            public Exception Failure { get; set; } = new InvalidOperationException("save failed");
            public bool FailLookup { get; set; }

            public Task<IReadOnlyCollection<Guid>> GetExistingAccessLogIdsAsync(IReadOnlyCollection<Guid> ids,
                CancellationToken cancellationToken)
                => Lookup(ids, AccessLogs.Select(a => a.Id));

            public Task<IReadOnlyCollection<Guid>> GetExistingDataAuditIdsAsync(IReadOnlyCollection<Guid> ids,
                CancellationToken cancellationToken)
                => Lookup(ids, DataAudits.Select(d => d.Id));

            private Task<IReadOnlyCollection<Guid>> Lookup(IReadOnlyCollection<Guid> ids, IEnumerable<Guid> stored)
            {
                if (FailLookup)
                {
                    throw new TimeoutException("lookup timed out");
                }
                return Task.FromResult<IReadOnlyCollection<Guid>>(ids.Intersect(stored).ToList());
            }

            public void Add(AccessLog accessLog) => _tracked.Add(accessLog);

            public void Add(DataAudit dataAudit) => _tracked.Add(dataAudit);

            public Task SaveChangesAsync(CancellationToken cancellationToken)
            {
                Attempts++;
                if (FailNextSaves > 0)
                {
                    FailNextSaves--;
                    throw Failure;
                }
                if (_tracked.OfType<AccessLog>().Any(a => StoredBehind.Contains(a.Id)))
                {
                    throw new UniqueViolation();
                }
                if (_tracked.OfType<DataAudit>().Any(d => d.Tbl == "Refused"))
                {
                    throw new InvalidOperationException("value refused by the database");
                }
                AccessLogs.AddRange(_tracked.OfType<AccessLog>());
                DataAudits.AddRange(_tracked.OfType<DataAudit>());
                _tracked.Clear();
                Saves++;
                return Task.CompletedTask;
            }

            public void ClearTracked() => _tracked.Clear();
        }
    }
}
