using FluentAssertions;
using Juice.Audit.Domain;
using Juice.Audit.EF;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Juice.Audit.Tests
{
    /// <summary>
    /// What a PostgreSQL error means for the records of a batch: the database's own problems are retried, a record's
    /// are refused. (SQL Server's <c>SqlException</c> can't be made outside the driver.)
    /// </summary>
    public sealed class WriteErrorClassifierTests
    {
        private static IAuditWriteErrorClassifier Classifier()
            => new ServiceCollection().AddEFAuditRepos().BuildServiceProvider()
                .GetRequiredService<IAuditWriteErrorClassifier>();

        private static PostgresException Error(string sqlState)
            => new("error", "ERROR", "ERROR", sqlState);

        [Theory(DisplayName = "PostgreSQL: an error of the database, not of a record, is retried once it is fixed")]
        [InlineData(PostgresErrorCodes.UndefinedTable)]
        [InlineData(PostgresErrorCodes.UndefinedColumn)]
        [InlineData(PostgresErrorCodes.InsufficientPrivilege)]
        [InlineData(PostgresErrorCodes.InvalidCatalogName)]
        [InlineData(PostgresErrorCodes.InvalidSchemaName)]
        [InlineData(PostgresErrorCodes.DiskFull)]
        [InlineData(PostgresErrorCodes.InvalidPassword)]
        public void Database_errors_are_the_database_s(string sqlState)
            // Disk full is also the driver's transient error: either way every record is retried.
            => Classifier().Classify(new Exception("wrapped", Error(sqlState))).Should()
                .BeOneOf(AuditWriteError.Database, AuditWriteError.Transient);

        [Theory(DisplayName = "PostgreSQL: a record's error stays the record's, and a key conflict is a duplicate")]
        [InlineData(PostgresErrorCodes.StringDataRightTruncation, AuditWriteError.Other)]
        [InlineData(PostgresErrorCodes.NotNullViolation, AuditWriteError.Other)]
        [InlineData(PostgresErrorCodes.InvalidTextRepresentation, AuditWriteError.Other)]
        [InlineData(PostgresErrorCodes.UniqueViolation, AuditWriteError.UniqueViolation)]
        public void Record_errors_are_the_record_s(string sqlState, AuditWriteError expected)
            => Classifier().Classify(Error(sqlState)).Should().Be(expected);
    }
}
