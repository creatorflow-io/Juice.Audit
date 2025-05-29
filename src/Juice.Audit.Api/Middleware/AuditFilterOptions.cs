using System.Data;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace Juice.Audit.AspNetCore.Middleware
{
    public class AuditFilterOptions
    {
        public int? ExecutionTimeThreshold { get; set; }
        public int RequestAbortedStatusCode { get; set; } = 408;
        public PathFilterEntry[] Filters { get; set; } = Array.Empty<PathFilterEntry>();

        public AuditFilterOptions Clear()
        {
            Filters = Array.Empty<PathFilterEntry>();
            return this;
        }

        public AuditFilterOptions Include(string path, params string[] methods)
        {
            var newFilters = new List<PathFilterEntry>(Filters)
            {
                new() {
                    Path = path,
                    Methods = methods,
                    Priority = Filters.Length
                }
            };
            Filters = newFilters.ToArray();
            return this;
        }

        public AuditFilterOptions Include(string path, int[] statusCodes, params string[] methods)
        {
            var newFilters = new List<PathFilterEntry>(Filters)
            {
                new() {
                    Path = path,
                    Methods = methods,
                    StatusCodes = statusCodes,
                    Priority = Filters.Length
                }
            };
            Filters = newFilters.ToArray();
            return this;
        }

        public AuditFilterOptions Exclude(string path, params string[] methods)
        {
            var newFilters = new List<PathFilterEntry>(Filters)
            {
                new() {
                    Path = path,
                    Methods = methods,
                    Priority = Filters.Length,
                    IsExcluded = true
                }
            };
            Filters = newFilters.ToArray();
            return this;
        }

        public AuditFilterOptions Exclude(string path, int[] statusCodes, params string[] methods)
        {
            var newFilters = new List<PathFilterEntry>(Filters)
            {
                new() {
                    Path = path,
                    Methods = methods,
                    StatusCodes = statusCodes,
                    Priority = Filters.Length,
                    IsExcluded = true
                }
            };
            Filters = newFilters.ToArray();
            return this;
        }

        public AuditFilterOptions Merge(params PathFilterEntry[] entries)
        {
            var newFilters = new List<PathFilterEntry>(Filters);
            newFilters.AddRange(entries.Where(e => !IsExists(e) || e.Priority != 0).ToArray());
            Filters = newFilters.ToArray();
            return this;
        }

        public bool IsMatch(string path, string method, out string? rule, out string? action, out IDictionary<string, string>? routeValues)
        {
            if (Filters.Length == 0 || ExecutionTimeThreshold.HasValue)
            {
                rule = null;
                action = null;
                routeValues = null;
                return true;
            }

            foreach (var filter in Filters.OrderByDescending(f => f.Priority))
            {
                if (filter.IsMatch(path, method, out var route, out routeValues))
                {
                    action = StringUtils.PathToAction(route);
                    rule = filter.Path;
                    return !filter.IsExcluded;
                }
            }
            rule = null;
            action = null;
            routeValues = null;
            return false;
        }

        public bool IsMatch(string path, string method, int statusCode, out string? rule, out string? action, out IDictionary<string, string>? routeValues)
        {
            if (Filters.Length == 0)
            {
                rule = null;
                action = null;
                routeValues = null;
                return true;
            }
            foreach (var filter in Filters.OrderByDescending(f => f.Priority))
            {
                if (filter.IsMatch(path, method, statusCode, out var route, out routeValues))
                {
                    rule = filter.Path;
                    action = StringUtils.PathToAction(route);
                    return !filter.IsExcluded;
                }
            }
            rule = null;
            action = null;
            routeValues = null;
            return false;
        }

        public bool IsExists(PathFilterEntry entry)
        {
            return Filters.Any(f => (f.Path == entry.Path || (f.IsGlobal && entry.IsGlobal))
                           && f.Methods.Length == entry.Methods.Length
                                          && f.Methods.All(m => entry.Methods.Contains(m, new StringComparer()))
                                                         && f.StatusCodes.Length == entry.StatusCodes.Length
                                                                        && f.StatusCodes.All(s => entry.StatusCodes.Contains(s))
                                                                                       && f.IsExcluded == entry.IsExcluded);
        }

        public string[] ReqHeaders = new string[] {
            ":authority:",
            "accept-#",
            "content-*",
            "x-forwarded-#",
            "referer",
            "user-agent"
        };

        public string[] ResHeaders = new string[]
        {
            "content-*"
        };

        public AuditFilterOptions StoreEmptyRequestHeaders()
        {
            ReqHeaders = Array.Empty<string>();
            return this;
        }

        public AuditFilterOptions StoreRequestHeaders(params string[] headers)
        {
            var newHeaders = new List<string>(ReqHeaders);
            newHeaders.AddRange(headers);
            ReqHeaders = newHeaders.ToArray();
            return this;
        }

        public AuditFilterOptions StoreEmptyResponseHeaders()
        {
            ResHeaders = Array.Empty<string>();
            return this;
        }

        public AuditFilterOptions StoreResponseHeaders(params string[] headers)
        {
            var newHeaders = new List<string>(ResHeaders);
            newHeaders.AddRange(headers);
            ResHeaders = newHeaders.ToArray();
            return this;
        }


        public bool IsReqHeaderMatch(string header)
        {
            return ReqHeaders.Any(h =>
                StringUtils.IsHeaderMatch(header, h));
        }

        public bool IsResHeaderMatch(string header)
        {
            return ResHeaders.Any(h =>
                StringUtils.IsHeaderMatch(header, h));
        }

    }

    public class PathFilterEntry
    {
        public int Priority { get; set; } = 0;
        public bool IsExcluded { get; set; } = false;
        public string Path { get; set; } = string.Empty;
        public string[] Methods { get; set; } = Array.Empty<string>();
        public int[] StatusCodes { get; set; } = Array.Empty<int>();
        public bool IsGlobal => Path == string.Empty;

        public bool IsMatch(string path, string method, out string? action, out IDictionary<string, string>? routeValues)
        {
            var match = StringUtils.IsPathMatch(path, Path, out action, out routeValues);
            return (IsGlobal || match)
                && (Methods.Length == 0 || Methods.Contains(method, new StringComparer()));
        }

        public bool IsMatch(string path, string method, int statusCode, out string? action, out IDictionary<string, string>? routeValues)
        {
            return IsMatch(path, method, out action, out routeValues)
                && (StatusCodes.Length == 0 || StatusCodes.Contains(statusCode));
        }
    }

    internal class StringComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
        {
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }
        public int GetHashCode(string obj)
        {
            return obj.GetHashCode();
        }
    }
}
