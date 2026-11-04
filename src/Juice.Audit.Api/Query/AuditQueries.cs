using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Juice.Audit.Domain.AccessLogAggregate;
using Juice.Audit.Domain.DataAuditAggregate;
using Juice.Audit.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Newtonsoft.Json.Linq;

namespace Juice.Audit.Api.Query
{
    /// <summary>A newest-first window of records, from the query string.</summary>
    internal sealed class AuditListArgs
    {
        public DateTimeOffset From { get; init; }
        public DateTimeOffset To { get; init; }
        public int Limit { get; init; } = 50;
        public AuditCursor? Cursor { get; init; }
        public Dictionary<string, string> Filters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public string? this[string name] => Filters.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Where the next page starts: rows at or before <see cref="At"/>, skipping the <see cref="Skip"/> rows at exactly
    /// <see cref="At"/> already returned. Rows at one time are ordered by id, so this needs no GUID comparison, which
    /// SQL Server and PostgreSQL order differently.
    /// </summary>
    internal sealed record AuditCursor(DateTimeOffset At, int Skip)
    {
        public override string ToString() => $"{At.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{Skip.ToString(CultureInfo.InvariantCulture)}";

        public static bool TryParse(string? text, out AuditCursor? cursor)
        {
            cursor = null;
            var parts = text?.Split('_');
            if (parts is not { Length: 2 }
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var skip)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }
            cursor = new AuditCursor(new DateTimeOffset(ticks, TimeSpan.Zero), skip);
            return true;
        }

        /// <summary>The cursor after a page whose rows are newest first.</summary>
        public static AuditCursor After(AuditCursor? previous, IReadOnlyList<DateTimeOffset> page)
        {
            var last = page[page.Count - 1];
            var atLast = page.Count(t => t == last);
            return new AuditCursor(last, atLast + (previous != null && previous.At == last ? previous.Skip : 0));
        }
    }

    /// <summary>The reads behind the audit API, over <see cref="AuditDbContext"/> (so scoped to the current tenant).</summary>
    internal static class AuditQueries
    {
        public const int MaxTraceAccessLogs = 100;
        public const int MaxTraceDataChanges = 500;

        private static readonly Regex TraceParent = new("^[0-9a-f]{2}-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The 32-hex trace id of a W3C <c>traceparent</c>, which audit records held before 10.4; any other id as is.
        /// </summary>
        public static string? PlainTraceId(string? traceId)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return traceId;
            }
            var trimmed = traceId.Trim();
            var match = TraceParent.Match(trimmed.ToLowerInvariant());
            return match.Success ? match.Groups[1].Value : trimmed;
        }

        #region Access logs

        public static async Task<AuditPage<AccessLogSummary>> AccessLogsAsync(AuditDbContext db, AuditListArgs args,
            CancellationToken token)
        {
            var query = db.AccessLogs.AsNoTracking().Where(x => x.DateTime >= args.From && x.DateTime < args.To);

            if (args["user"] is { } user) { query = query.Where(x => x.User != null && x.User.Contains(user)); }
            if (args["action"] is { } action) { query = query.Where(x => x.Action.Contains(action)); }
            if (args["method"] is { } method)
            {
                method = method.ToUpperInvariant();
                query = query.Where(x => x.Request!.Method == method);
            }
            if (args["path"] is { } path) { query = query.Where(x => x.Request!.Path.Contains(path)); }
            if (args["app"] is { } app) { query = query.Where(x => x.Server!.App == app); }
            if (args["tenant"] is { } tenant) { query = query.Where(x => x.TenantId == tenant); }
            if (args["restricted"] is { } restricted && bool.TryParse(restricted, out var isRestricted))
            {
                query = query.Where(x => x.IsRestricted == isRestricted);
            }
            if (args["minMs"] is { } minMs && long.TryParse(minMs, out var min))
            {
                query = query.Where(x => x.Response!.ElapsedMs >= min);
            }
            if (args["status"] is { } status) { query = WhereStatus(query, status); }
            if (args["traceId"] is { } traceId) { query = WhereTrace(query, traceId); }

            if (args.Cursor != null)
            {
                var at = args.Cursor.At;
                query = query.Where(x => x.DateTime <= at);
            }
            var rows = await query
                .OrderByDescending(x => x.DateTime).ThenBy(x => x.Id)
                .Skip(args.Cursor?.Skip ?? 0)
                .Take(args.Limit + 1)
                .Select(SummaryOf)
                .ToListAsync(token);
            return Page(rows.Select(r => r with { TraceId = PlainTraceId(r.TraceId) }).ToList(), args,
                r => r.DateTime);
        }

        public static async Task<AccessLogDetail?> AccessLogAsync(AuditDbContext db, Guid id, CancellationToken token)
        {
            var log = await db.AccessLogs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, token);
            if (log == null)
            {
                return null;
            }
            var traceId = log.Request?.TraceId;
            var changes = string.IsNullOrEmpty(traceId) ? 0
                : await db.AuditEntries.AsNoTracking().CountAsync(x => x.TraceId == traceId, token);
            return DetailOf(log, changes);
        }

        private static IQueryable<AccessLog> WhereStatus(IQueryable<AccessLog> query, string status)
        {
            status = status.Trim().ToLowerInvariant();
            if (status == "error")
            {
                return query.Where(x => x.Response!.Status >= 400);
            }
            if (status.Length == 3 && status.EndsWith("xx") && char.IsDigit(status[0]))
            {
                var low = (status[0] - '0') * 100;
                var high = low + 100;
                return query.Where(x => x.Response!.Status >= low && x.Response!.Status < high);
            }
            return int.TryParse(status, out var code) ? query.Where(x => x.Response!.Status == code) : query;
        }

        private static IQueryable<AccessLog> WhereTrace(IQueryable<AccessLog> query, string traceId)
        {
            var raw = traceId.Trim();
            var plain = PlainTraceId(raw)!;
            var parent = $"00-{plain}-";
            return query.Where(x => x.Request!.TraceId == raw || x.Request!.TraceId == plain
                || x.Request!.TraceId.StartsWith(parent));
        }

        private static readonly System.Linq.Expressions.Expression<Func<AccessLog, AccessLogSummary>> SummaryOf =
            x => new AccessLogSummary(x.Id, x.DateTime, x.User, x.TenantId, x.Action,
                x.Request!.Method, x.Request!.Path, (int?)x.Response!.Status, x.Response!.ElapsedMs, x.Server!.App,
                x.Request!.TraceId, x.IsRestricted);

        private static AccessLogDetail DetailOf(AccessLog log, int dataChanges) => new(
            log.Id, log.DateTime, log.User, log.TenantId, log.Action, PlainTraceId(log.Request?.TraceId),
            log.IsRestricted, MetadataOf(log.Metadata),
            log.Request == null ? null : new AccessLogRequest(log.Request.Method, log.Request.Scheme, log.Request.Host,
                log.Request.Path, log.Request.Query, log.Request.Headers, log.Request.Data, log.Request.RIPA,
                log.Request.Zone),
            log.Response == null ? null : new AccessLogResponse(log.Response.Status, log.Response.ElapsedMs,
                log.Response.Msg, log.Response.Err, log.Response.Headers, log.Response.Data),
            log.Server == null ? null : new AccessLogServer(log.Server.App, log.Server.AppVer, log.Server.Machine,
                log.Server.OS),
            dataChanges);

        private static IReadOnlyDictionary<string, string?> MetadataOf(JObject? metadata)
        {
            var values = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            if (metadata == null)
            {
                return values;
            }
            foreach (var property in metadata.Properties())
            {
                values[property.Name] = property.Value.Type switch
                {
                    JTokenType.Null or JTokenType.Undefined => null,
                    JTokenType.String => property.Value.Value<string>(),
                    _ => property.Value.ToString(Newtonsoft.Json.Formatting.None),
                };
            }
            return values;
        }

        #endregion

        #region Data changes

        private sealed record DataChangeRow(Guid Id, DateTimeOffset DateTime, string? User, string? TenantId,
            string Action, string? Database, string? Schema, string Table, string Keys, string? Changes, string? TraceId);

        public static async Task<AuditPage<DataChangeSummary>> DataChangesAsync(AuditDbContext db, AuditListArgs args,
            CancellationToken token)
        {
            var query = DataAudits(db, args["key"]).Where(x => x.DateTime >= args.From && x.DateTime < args.To);

            if (args["user"] is { } user) { query = query.Where(x => x.User != null && x.User.Contains(user)); }
            if (args["action"] is { } action) { query = query.Where(x => x.Action == action); }
            if (args["database"] is { } database) { query = query.Where(x => x.Db == database); }
            if (args["schema"] is { } schema) { query = query.Where(x => x.Schema == schema); }
            if (args["table"] is { } table) { query = query.Where(x => x.Tbl.Contains(table)); }
            if (args["tenant"] is { } tenant) { query = query.Where(x => x.TenantId == tenant); }
            if (args["traceId"] is { } traceId) { query = WhereTrace(query, traceId); }

            if (args.Cursor != null)
            {
                var at = args.Cursor.At;
                query = query.Where(x => x.DateTime <= at);
            }
            var rows = await query
                .OrderByDescending(x => x.DateTime).ThenBy(x => x.Id)
                .Skip(args.Cursor?.Skip ?? 0)
                .Take(args.Limit + 1)
                .Select(x => new DataChangeRow(x.Id, x.DateTime, x.User, x.TenantId, x.Action, x.Db, x.Schema, x.Tbl,
                    x.Kvps, null, x.TraceId))
                .ToListAsync(token);
            return Page(rows.Select(r => new DataChangeSummary(r.Id, r.DateTime, r.User, r.TenantId, r.Action,
                r.Database, r.Schema, r.Table, Json(r.Keys), PlainTraceId(r.TraceId))).ToList(), args, r => r.DateTime);
        }

        public static async Task<DataChangeDetail?> DataChangeAsync(AuditDbContext db, Guid id, CancellationToken token)
        {
            var change = await db.AuditEntries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, token);
            return change == null ? null : DetailOf(change);
        }

        private static DataChangeDetail DetailOf(DataAudit x) => new(x.Id, x.DateTime, x.User, x.TenantId, x.Action,
            x.Db, x.Schema, x.Tbl, Json(x.Kvps), Json(x.Changes), PlainTraceId(x.TraceId));

        /// <summary>
        /// The data changes, narrowed to those whose keys contain <paramref name="key"/>. The keys are a JSON column:
        /// text on SQL Server, where Contains works, but jsonb on PostgreSQL, which has to be cast to text first.
        /// </summary>
        private static IQueryable<DataAudit> DataAudits(AuditDbContext db, string? key)
        {
            if (key == null)
            {
                return db.AuditEntries.AsNoTracking();
            }
            if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return db.AuditEntries.AsNoTracking().Where(x => x.Kvps.Contains(key));
            }
            var entity = db.Model.FindEntityType(typeof(DataAudit))!;
            var table = entity.GetTableName()!;
            var schema = entity.GetSchema();
            var column = entity.FindProperty(nameof(DataAudit.Kvps))!
                .GetColumnName(StoreObjectIdentifier.Table(table, schema));
            static string Quote(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
            var from = schema == null ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";
            var pattern = "%" + key.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            return db.AuditEntries
                .FromSqlRaw($"SELECT * FROM {from} WHERE {Quote(column!)}::text LIKE {{0}}", pattern)
                .AsNoTracking();
        }

        private static IQueryable<DataAudit> WhereTrace(IQueryable<DataAudit> query, string traceId)
        {
            var raw = traceId.Trim();
            var plain = PlainTraceId(raw)!;
            var parent = $"00-{plain}-";
            return query.Where(x => x.TraceId == raw || x.TraceId == plain || x.TraceId!.StartsWith(parent));
        }

        #endregion

        #region Trace and overview

        public static async Task<AuditTrace> TraceAsync(AuditDbContext db, string traceId, CancellationToken token)
        {
            var logs = await WhereTrace(db.AccessLogs.AsNoTracking(), traceId)
                .OrderBy(x => x.DateTime).ThenBy(x => x.Id)
                .Take(MaxTraceAccessLogs + 1)
                .ToListAsync(token);
            var changes = await WhereTrace(db.AuditEntries.AsNoTracking(), traceId)
                .OrderBy(x => x.DateTime).ThenBy(x => x.Id)
                .Take(MaxTraceDataChanges + 1)
                .ToListAsync(token);
            var truncated = logs.Count > MaxTraceAccessLogs || changes.Count > MaxTraceDataChanges;
            logs = logs.Take(MaxTraceAccessLogs).ToList();
            changes = changes.Take(MaxTraceDataChanges).ToList();
            return new AuditTrace(PlainTraceId(traceId)!,
                logs.Select(l => DetailOf(l, changes.Count(c => c.TraceId == l.Request?.TraceId))).ToList(),
                changes.Select(DetailOf).ToList(),
                truncated);
        }

        public static async Task<AuditOverview> OverviewAsync(AuditDbContext db, DateTimeOffset from, DateTimeOffset to,
            CancellationToken token)
        {
            const int top = 10;
            var logs = db.AccessLogs.AsNoTracking().Where(x => x.DateTime >= from && x.DateTime < to);
            var changes = db.AuditEntries.AsNoTracking().Where(x => x.DateTime >= from && x.DateTime < to);

            var requests = await logs.CountAsync(token);
            var clientErrors = await logs.Where(x => x.Response!.Status >= 400 && x.Response!.Status < 500).CountAsync(token);
            var serverErrors = await logs.Where(x => x.Response!.Status >= 500).CountAsync(token);
            var restricted = await logs.Where(x => x.IsRestricted).CountAsync(token);
            var dataChanges = await changes.CountAsync(token);

            // EF cannot read an owned type's column inside a GroupBy's aggregate or key, so each status class is a
            // count of its own, and errors per app or user are a second grouping of the failed requests.
            var statuses = new List<AuditCount>();
            for (var c = 1; c <= 5; c++)
            {
                var (low, high) = (c * 100, c * 100 + 100);
                var count = await logs.Where(x => x.Response!.Status >= low && x.Response!.Status < high).CountAsync(token);
                if (count > 0)
                {
                    statuses.Add(new AuditCount($"{c}xx", count, c >= 4 ? count : 0));
                }
            }
            var other = requests - statuses.Sum(s => s.Count);
            if (other > 0)
            {
                statuses.Add(new AuditCount("other", other));
            }
            var failed = logs.Where(x => x.Response!.Status >= 400);

            var appCounts = await logs.GroupBy(x => x.Server!.App)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count).Take(top)
                .ToListAsync(token);
            var appErrors = (await failed.GroupBy(x => x.Server!.App)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToListAsync(token))
                .ToDictionary(g => g.Key ?? "", g => g.Count);
            var apps = appCounts.Select(g => new AuditCount(g.Key ?? "", g.Count,
                appErrors.TryGetValue(g.Key ?? "", out var e) ? e : 0)).ToList();

            var userCounts = await logs.GroupBy(x => x.User)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count).Take(top)
                .ToListAsync(token);
            var userErrors = (await failed.GroupBy(x => x.User)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToListAsync(token))
                .ToDictionary(g => g.Key ?? "", g => g.Count);
            var users = userCounts.Select(g => new AuditCount(g.Key ?? "", g.Count,
                userErrors.TryGetValue(g.Key ?? "", out var e) ? e : 0)).ToList();
            var tables = (await changes.GroupBy(x => new { x.Schema, x.Tbl })
                    .Select(g => new { g.Key.Schema, g.Key.Tbl, Count = g.Count() })
                    .OrderByDescending(g => g.Count).Take(top)
                    .ToListAsync(token))
                .Select(g => new AuditCount(string.IsNullOrEmpty(g.Schema) ? g.Tbl : $"{g.Schema}.{g.Tbl}", g.Count))
                .ToList();
            var actions = (await changes.GroupBy(x => x.Action)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .ToListAsync(token))
                .Select(g => new AuditCount(g.Key, g.Count)).ToList();

            var recentErrors = await logs.Where(x => x.Response!.Status >= 400)
                .OrderByDescending(x => x.DateTime).ThenBy(x => x.Id).Take(top)
                .Select(SummaryOf).ToListAsync(token);
            var slowest = await logs.Where(x => x.Response!.ElapsedMs != null)
                .OrderByDescending(x => x.Response!.ElapsedMs).ThenBy(x => x.Id).Take(top)
                .Select(SummaryOf).ToListAsync(token);

            return new AuditOverview(from, to, requests, clientErrors, serverErrors, restricted, dataChanges,
                statuses, apps, users, tables, actions,
                recentErrors.Select(r => r with { TraceId = PlainTraceId(r.TraceId) }).ToList(),
                slowest.Select(r => r with { TraceId = PlainTraceId(r.TraceId) }).ToList());
        }

        #endregion

        private static AuditPage<T> Page<T>(List<T> rows, AuditListArgs args, Func<T, DateTimeOffset> timeOf)
        {
            if (rows.Count <= args.Limit)
            {
                return new AuditPage<T>(rows, null);
            }
            rows.RemoveAt(rows.Count - 1);
            return new AuditPage<T>(rows, AuditCursor.After(args.Cursor, rows.Select(timeOf).ToList()).ToString());
        }

        private static JsonNode? Json(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            try
            {
                return JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                return JsonValue.Create(text);
            }
        }
    }
}
