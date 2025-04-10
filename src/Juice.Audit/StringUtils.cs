using System.Text.RegularExpressions;

namespace Juice.Audit
{
    public static class StringUtils
    {
        public static bool IsHeaderMatch(string header, string pattern)
        {
            return Regex.IsMatch(header,
               "^" + pattern
                            .Replace("*", "([^-]+){1}")
                            .Replace("-#", "(-[^-]+)*")
                            .Replace("#-", "([^-]+-)*") + "$", RegexOptions.IgnoreCase);
        }

        public static bool IsPathMatch(string path, string expected, out string? route, out IDictionary<string, string>? routeValues)
        {
            // Replace all '*' in expected with indexed groups

            var pattern = @"\{(\w+)\}";
            var replacement = @"(?<$1>[^\\/]+)";
            var newExpected = Regex.Replace(expected, pattern, replacement)
                .Replace("*", "([^\\/]+){1}")
                .Replace("/#", "(\\/[^\\/]+)*")
                .Replace("#/", "([^\\/]+\\/)*");

            // Remove indexed groups from path
            var regex = new Regex("^" + newExpected + "$", RegexOptions.IgnoreCase);
            var match = regex.Match(path);

            if (!match.Success)
            {
                route = null;
                routeValues = null;
                return false;
            }

            route = path;
            routeValues = new Dictionary<string, string>();

            foreach (var groupName in regex.GetGroupNames())
            {
                // Skip numeric group names (0, 1, 2, etc.)
                if (!int.TryParse(groupName, out _))
                {
                    routeValues[groupName] = match.Groups[groupName].Value;
                    route = route.Replace(match.Groups[groupName].Value, $"{{{groupName}}}");
                }
            }
            return true;
        }

        public static Stream GenerateStreamFromString(string? s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        public static string? PathToAction(string? path)
        {
            return path?.Trim('/').Replace("/", "_");
        }
    }
}
