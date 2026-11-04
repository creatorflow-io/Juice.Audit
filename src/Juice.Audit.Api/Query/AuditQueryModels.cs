using System.Text.Json.Nodes;

namespace Juice.Audit.Api.Query
{
    /// <summary>A page of a newest-first list; pass <see cref="NextCursor"/> as <c>cursor</c> for the next one.</summary>
    public sealed record AuditPage<T>(IReadOnlyList<T> Items, string? NextCursor);

    /// <summary>An access record in a list: one audited HTTP request, message, mediator request or notification.</summary>
    public sealed record AccessLogSummary(
        Guid Id,
        DateTimeOffset DateTime,
        string? User,
        string? TenantId,
        string Action,
        string? Method,
        string? Path,
        int? Status,
        long? ElapsedMs,
        string? App,
        string? TraceId,
        bool IsRestricted);

    public sealed record AccessLogRequest(string? Method, string? Scheme, string? Host, string? Path, string? Query,
        string? Headers, string? Data, string? RemoteIp, string? Zone);

    public sealed record AccessLogResponse(int? Status, long? ElapsedMs, string? Message, string? Error, string? Headers,
        string? Data);

    public sealed record AccessLogServer(string? App, string? AppVersion, string? Machine, string? OS);

    /// <summary>An access record with everything it holds, and how many data changes share its trace id.</summary>
    public sealed record AccessLogDetail(
        Guid Id,
        DateTimeOffset DateTime,
        string? User,
        string? TenantId,
        string Action,
        string? TraceId,
        bool IsRestricted,
        IReadOnlyDictionary<string, string?> Metadata,
        AccessLogRequest? Request,
        AccessLogResponse? Response,
        AccessLogServer? Server,
        int DataChanges);

    /// <summary>A data change in a list: the row's keys, without its values.</summary>
    public sealed record DataChangeSummary(
        Guid Id,
        DateTimeOffset DateTime,
        string? User,
        string? TenantId,
        string Action,
        string? Database,
        string? Schema,
        string Table,
        JsonNode? Keys,
        string? TraceId);

    /// <summary>
    /// A data change with its values: <c>Changes</c> is <c>{ "OriginalValues": {…}, "CurrentValues": {…} }</c> as the
    /// audited app reported them.
    /// </summary>
    public sealed record DataChangeDetail(
        Guid Id,
        DateTimeOffset DateTime,
        string? User,
        string? TenantId,
        string Action,
        string? Database,
        string? Schema,
        string Table,
        JsonNode? Keys,
        JsonNode? Changes,
        string? TraceId);

    /// <summary>Everything audited under one trace id: its access records and the data they changed.</summary>
    public sealed record AuditTrace(string TraceId, IReadOnlyList<AccessLogDetail> AccessLogs,
        IReadOnlyList<DataChangeDetail> DataChanges, bool Truncated);

    public sealed record AuditCount(string Name, int Count, int Errors = 0);

    /// <summary>Totals and the top rows over a time window.</summary>
    public sealed record AuditOverview(
        DateTimeOffset From,
        DateTimeOffset To,
        int Requests,
        int ClientErrors,
        int ServerErrors,
        int Restricted,
        int DataChanges,
        IReadOnlyList<AuditCount> Statuses,
        IReadOnlyList<AuditCount> Apps,
        IReadOnlyList<AuditCount> Users,
        IReadOnlyList<AuditCount> Tables,
        IReadOnlyList<AuditCount> ChangeActions,
        IReadOnlyList<AccessLogSummary> RecentErrors,
        IReadOnlyList<AccessLogSummary> Slowest);
}
