using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Juice.Audit.Collector
{
    /// <summary>
    /// Masks the configured headers (<see cref="AuditCollectorOptions.RedactHeaders"/>) in an access record's request
    /// and response headers, so credentials sent by any app never reach the audit database. The masked names go in
    /// the record's <c>Redacted</c> metadata.
    /// </summary>
    public sealed class HeaderRedactionProcessor : IAuditRecordProcessor
    {
        /// <summary>A JSON string, or the start of one that was cut off.</summary>
        private const string JsonString = @"""(?:[^""\\]|\\.)*(?:""|\\?$)";

        private readonly HashSet<string> _headers;
        private readonly string _mask;
        private readonly Regex? _byName;

        public HeaderRedactionProcessor(IOptions<AuditCollectorOptions> options)
        {
            _headers = new HashSet<string>(options.Value.RedactHeaders ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            _mask = options.Value.RedactMask ?? "***";
            if (_headers.Count > 0)
            {
                // "Name": "value" or "Name": ["value", …], whole or cut off, for headers that are no longer valid JSON.
                var names = string.Join("|", _headers.Select(Regex.Escape));
                _byName = new Regex(
                    $@"(?<key>""(?<name>{names})""\s*:\s*)(?:{JsonString}|\[\s*(?:{JsonString}\s*,?\s*)*(?:\]|$)|null)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
            }
        }

        public int Order => 100;

        public ValueTask<ProcessorResult> ProcessAsync(AuditRecordEnvelope record, CancellationToken cancellationToken)
        {
            var access = record.Access;
            if (access == null || _headers.Count == 0)
            {
                return new(ProcessorResult.Continue);
            }
            var redacted = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (access.Request is { } request && Redact(request.Headers, redacted, out var requestHeaders))
            {
                request.Rewrite(request.Data, requestHeaders);
            }
            if (access.Response is { } response && Redact(response.Headers, redacted, out var responseHeaders))
            {
                response.Rewrite(response.Data, response.Err, responseHeaders);
            }
            if (redacted.Count > 0)
            {
                access.Metadata["Redacted"] = new JArray(redacted);
            }
            return new(ProcessorResult.Continue);
        }

        /// <summary>Masks the matching headers; <c>false</c> when nothing matched.</summary>
        private bool Redact(string? headers, ISet<string> redacted, out string? result)
        {
            result = headers;
            if (string.IsNullOrWhiteSpace(headers))
            {
                return false;
            }
            JObject json;
            try
            {
                json = JObject.Parse(headers);
            }
            catch (JsonException)
            {
                // Not a JSON object: most often one the app cut to the column's length, which leaves it unparsable.
                // Mask by name in the text, or the credentials in it would be stored as they came.
                return RedactText(headers, redacted, out result);
            }
            var changed = false;
            foreach (var property in json.Properties())
            {
                if (_headers.Contains(property.Name))
                {
                    property.Value = _mask;
                    redacted.Add(property.Name);
                    changed = true;
                }
            }
            if (changed)
            {
                result = json.ToString(Formatting.None);
            }
            return changed;
        }

        private bool RedactText(string headers, ISet<string> redacted, out string? result)
        {
            var changed = false;
            var mask = JsonConvert.ToString(_mask);
            result = _byName!.Replace(headers, match =>
            {
                changed = true;
                redacted.Add(match.Groups["name"].Value);
                return match.Groups["key"].Value + mask;
            });
            return changed;
        }
    }
}
