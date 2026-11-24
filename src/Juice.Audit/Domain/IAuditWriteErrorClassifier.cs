namespace Juice.Audit.Domain
{
    /// <summary>What a failed write means for the records in it.</summary>
    public enum AuditWriteError
    {
        /// <summary>Not known: retried record by record so one bad record doesn't fail the others.</summary>
        Other,
        /// <summary>A record with the same id is already stored: a duplicate, not an error.</summary>
        UniqueViolation,
        /// <summary>The database is unavailable, timed out or asked for a retry: retry later.</summary>
        Transient,
        /// <summary>
        /// The database, not the record: a missing table or column (a migration not run), no permission, no such
        /// database, no space. Every record would fail the same way: retry later, once someone fixes it.
        /// </summary>
        Database
    }

    /// <summary>
    /// Tells what a database exception means, so <c>Juice.Audit</c> stays free of EF and of the database providers.
    /// Implemented by <c>Juice.Audit.EF</c> for SQL Server and PostgreSQL.
    /// </summary>
    public interface IAuditWriteErrorClassifier
    {
        AuditWriteError Classify(Exception exception);
    }
}
