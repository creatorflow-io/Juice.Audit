using System.Globalization;
using Juice.Audit.Api.Query;
using Juice.Audit.EF;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.AspNetCore.Builder
{
    public static class AuditQueryEndpointRouteBuilderExtensions
    {
        /// <summary>The longest window the lists and the overview read.</summary>
        public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(93);

        /// <summary>
        /// Maps the read API of the audit records (the <c>@juice-js/audit</c> pages read it) under <paramref name="prefix"/>:
        /// <list type="bullet">
        /// <item><c>GET access-logs</c>, <c>GET access-logs/{id}</c></item>
        /// <item><c>GET data-changes</c>, <c>GET data-changes/{id}</c></item>
        /// <item><c>GET traces/{traceId}</c>: the access records and data changes of one trace</item>
        /// <item><c>GET overview</c>: totals and top rows over a window</item>
        /// </list>
        /// Lists take <c>from</c>, <c>to</c> (ISO 8601; default the last 24 hours), <c>limit</c> (1–200, default 50) and
        /// <c>cursor</c>, and return newest first with a <c>nextCursor</c>. The records are read through
        /// <see cref="AuditDbContext"/>, so a tenant sees only its own and the root tenant all of them; register it
        /// with <c>ConfigureAuditDefault()</c> or <c>ConfigureAuditGrpcHost()</c>.
        /// </summary>
        /// <param name="endpoints">The app.</param>
        /// <param name="prefix">Where to map them.</param>
        /// <param name="policy">The authorization policy a caller needs; without one, the default policy (by default,
        /// any authenticated user).</param>
        /// <returns>A builder for further conventions on all the endpoints, such as CORS or rate limiting.</returns>
        public static IEndpointConventionBuilder MapJuiceAudit(this IEndpointRouteBuilder endpoints,
            string prefix = "/api/audit", string? policy = null)
        {
            prefix = "/" + prefix.Trim('/');
            var builders = new List<IEndpointConventionBuilder>
            {
                endpoints.MapGet($"{prefix}/access-logs", async (HttpContext http, AuditDbContext db, CancellationToken token) =>
                    TryArgs(http.Request, out var args, out var problem)
                        ? Results.Ok(await AuditQueries.AccessLogsAsync(db, args!, token))
                        : problem!),

                endpoints.MapGet($"{prefix}/access-logs/{{id:guid}}", async (Guid id, AuditDbContext db, CancellationToken token) =>
                    await AuditQueries.AccessLogAsync(db, id, token) is { } log ? Results.Ok(log) : NotFound("access record", id)),

                endpoints.MapGet($"{prefix}/data-changes", async (HttpContext http, AuditDbContext db, CancellationToken token) =>
                    TryArgs(http.Request, out var args, out var problem)
                        ? Results.Ok(await AuditQueries.DataChangesAsync(db, args!, token))
                        : problem!),

                endpoints.MapGet($"{prefix}/data-changes/{{id:guid}}", async (Guid id, AuditDbContext db, CancellationToken token) =>
                    await AuditQueries.DataChangeAsync(db, id, token) is { } change ? Results.Ok(change) : NotFound("data change", id)),

                endpoints.MapGet($"{prefix}/traces/{{traceId}}", async (string traceId, AuditDbContext db, CancellationToken token) =>
                    Results.Ok(await AuditQueries.TraceAsync(db, traceId, token))),

                endpoints.MapGet($"{prefix}/overview", async (HttpContext http, AuditDbContext db, CancellationToken token) =>
                    TryArgs(http.Request, out var args, out var problem)
                        ? Results.Ok(await AuditQueries.OverviewAsync(db, args!.From, args.To, token))
                        : problem!),
            };

            var all = new AuditEndpointConventionBuilder(builders);
            if (policy == null)
            {
                all.RequireAuthorization();
            }
            else
            {
                all.RequireAuthorization(policy);
            }
            return all;
        }

        private static IResult NotFound(string what, Guid id)
            => Results.Problem(title: "Not found", detail: $"No {what} {id}.", statusCode: StatusCodes.Status404NotFound);

        private static IResult BadRequest(string detail)
            => Results.Problem(title: "Invalid query", detail: detail, statusCode: StatusCodes.Status400BadRequest);

        private static readonly HashSet<string> Paging = new(StringComparer.OrdinalIgnoreCase) { "from", "to", "limit", "cursor" };

        private static bool TryArgs(HttpRequest request, out AuditListArgs? args, out IResult? problem)
        {
            args = null;
            problem = null;
            var query = request.Query;

            var to = DateTimeOffset.UtcNow;
            if (query.TryGetValue("to", out var toText) && !TryTime(toText, out to))
            {
                problem = BadRequest("'to' is not an ISO 8601 time.");
                return false;
            }
            var from = to.AddDays(-1);
            if (query.TryGetValue("from", out var fromText) && !TryTime(fromText, out from))
            {
                problem = BadRequest("'from' is not an ISO 8601 time.");
                return false;
            }
            if (from > to)
            {
                problem = BadRequest("'from' is after 'to'.");
                return false;
            }
            if (to - from > MaxWindow)
            {
                problem = BadRequest($"The window is longer than {MaxWindow.TotalDays} days.");
                return false;
            }

            var limit = 50;
            if (query.TryGetValue("limit", out var limitText)
                && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > 200))
            {
                problem = BadRequest("'limit' is a number from 1 to 200.");
                return false;
            }

            AuditCursor? cursor = null;
            if (query.TryGetValue("cursor", out var cursorText) && !AuditCursor.TryParse(cursorText, out cursor))
            {
                problem = BadRequest("'cursor' is not one this API returned.");
                return false;
            }

            var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in query)
            {
                var value = values.ToString().Trim();
                if (!Paging.Contains(name) && value.Length > 0)
                {
                    filters[name] = value;
                }
            }
            args = new AuditListArgs { From = from, To = to, Limit = limit, Cursor = cursor, Filters = filters };
            return true;
        }

        private static bool TryTime(string? text, out DateTimeOffset time)
        {
            var ok = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
            time = time.ToUniversalTime();
            return ok;
        }
    }

    /// <summary>Applies a convention to every endpoint <c>MapJuiceAudit()</c> mapped.</summary>
    internal sealed class AuditEndpointConventionBuilder : IEndpointConventionBuilder
    {
        private readonly IReadOnlyList<IEndpointConventionBuilder> _builders;

        public AuditEndpointConventionBuilder(IReadOnlyList<IEndpointConventionBuilder> builders) => _builders = builders;

        public void Add(Action<EndpointBuilder> convention)
        {
            foreach (var builder in _builders)
            {
                builder.Add(convention);
            }
        }

#if NET7_0_OR_GREATER
        public void Finally(Action<EndpointBuilder> finallyConvention)
        {
            foreach (var builder in _builders)
            {
                builder.Finally(finallyConvention);
            }
        }
#endif
    }
}
