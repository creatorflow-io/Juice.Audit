using System.Data.Common;
using Juice.Audit.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Juice.Audit.EF
{
    /// <summary>
    /// Database errors on SQL Server and PostgreSQL: a key conflict (SQL Server 2627/2601, PostgreSQL 23505) is a
    /// duplicate; connection loss, timeouts and the providers' own transient errors are retryable.
    /// </summary>
    internal sealed class AuditWriteErrorClassifier : IAuditWriteErrorClassifier
    {
        public AuditWriteError Classify(Exception exception)
        {
            for (var ex = exception; ex != null; ex = ex.InnerException)
            {
                switch (ex)
                {
                    case SqlException { Number: 2627 or 2601 }:
                    case PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }:
                        return AuditWriteError.UniqueViolation;
                    case RetryLimitExceededException:
                    case TimeoutException:
                    case DbException { IsTransient: true }:
                        return AuditWriteError.Transient;
                    case SqlException sql when IsTransient(sql):
                        return AuditWriteError.Transient;
                    case SqlException sql when SqlServerDatabaseErrors.Contains(sql.Number):
                    case PostgresException pg when IsDatabaseError(pg.SqlState):
                        return AuditWriteError.Database;
                }
            }
            return AuditWriteError.Other;
        }

        /// <summary>
        /// SQL Server errors of the database, not of a record: invalid object (208) or column (207), permission denied
        /// (229, 230), cannot open the database (4060), login failed (18456), no space (1105, 9002).
        /// </summary>
        private static readonly HashSet<int> SqlServerDatabaseErrors = new() { 207, 208, 229, 230, 1105, 4060, 9002, 18456 };

        /// <summary>
        /// PostgreSQL classes of the database, not of a record: 42 (undefined table or column, no privilege, a schema
        /// that doesn't match), 28 (authorization), 3D (no such database), 3F (no such schema), 53 (no space or
        /// memory), 57 (shut down by an operator). A value too long (22001) or a broken constraint (23) stays the
        /// record's.
        /// </summary>
        private static bool IsDatabaseError(string? sqlState)
            => sqlState is { Length: 5 } && sqlState[..2] is "42" or "28" or "3D" or "3F" or "53" or "57";

#pragma warning disable EF1001 // the detector EF's own retrying execution strategy uses
        private static bool IsTransient(SqlException exception)
            => Microsoft.EntityFrameworkCore.SqlServer.Storage.Internal.SqlServerTransientExceptionDetector.ShouldRetryOn(exception);
#pragma warning restore EF1001
    }
}
