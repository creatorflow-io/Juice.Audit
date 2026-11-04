using System.Reflection;
using Juice.Audit.AspNetCore.Middleware;

namespace Juice.Audit.Api.Scopes
{
    /// <summary>
    /// How the audit scopes opened outside an HTTP request (mediator requests and notifications, messages) are saved.
    /// They follow <c>UseAudit()</c>'s rules: a record is saved when data changed, an access log or a time log was
    /// requested, <see cref="ExecutionTimeThreshold"/> was exceeded, or a <see cref="Filter"/> rule matched.
    /// </summary>
    public class AuditScopeOptions
    {
        /// <summary>The app on the records (<c>ServerInfo.App</c>); the entry assembly's name by default.</summary>
        public string? AppName { get; set; } = Assembly.GetEntryAssembly()?.GetName().Name;

        /// <summary>Saves the record and the time log when the scope takes longer, in milliseconds.</summary>
        public int? ExecutionTimeThreshold { get; set; }

        /// <summary>
        /// Rules matched against the scope's path (the request's, notification's or event's name) and method
        /// (<see cref="AuditScopeMethods"/>), as <c>UseAudit()</c>'s are matched against a request's. <c>null</c> (the
        /// default) matches nothing. Without rules, an <see cref="AuditFilterOptions"/> matches everything.
        /// </summary>
        public AuditFilterOptions? Filter { get; set; }
    }

    /// <summary>The access record's request method for a scope opened outside an HTTP request.</summary>
    public static class AuditScopeMethods
    {
        public const string Request = "REQUEST";
        public const string Notification = "NOTIFICATION";
        public const string Message = "MESSAGE";
    }
}
