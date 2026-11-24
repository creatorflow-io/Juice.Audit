using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Juice.Audit.Collector
{
    /// <summary>
    /// Cuts request bodies, response bodies and errors longer than <see cref="AuditCollectorOptions.MaxBodyLength"/>
    /// characters, noting the original sizes in the access record's <c>Truncated</c> metadata
    /// (<c>{"Req_Data": n, …}</c>), and data changes over that many bytes, which keep valid JSON
    /// (<c>{"$truncated":true,"$originalSize":n,"$partial":"…"}</c>).
    /// </summary>
    public sealed class TruncationProcessor : IAuditRecordProcessor
    {
        private readonly int _max;

        public TruncationProcessor(IOptions<AuditCollectorOptions> options)
        {
            _max = options.Value.MaxBodyLength;
        }

        public int Order => 200;

        public ValueTask<ProcessorResult> ProcessAsync(AuditRecordEnvelope record, CancellationToken cancellationToken)
        {
            if (_max <= 0)
            {
                return new(ProcessorResult.Continue);
            }
            if (record.Data is { } data)
            {
                var changes = AuditTruncation.TruncateJson(data.Changes, _max);
                if (!ReferenceEquals(changes, data.Changes))
                {
                    data.RewriteChanges(changes);
                }
            }
            if (record.Access is { } access)
            {
                var sizes = new JObject();
                if (access.Request is { } request)
                {
                    var body = Cut(request.Data, "Req_Data", sizes);
                    if (!ReferenceEquals(body, request.Data))
                    {
                        request.Rewrite(body, request.Headers);
                    }
                }
                if (access.Response is { } response)
                {
                    var body = Cut(response.Data, "Res_Data", sizes);
                    var error = Cut(response.Err, "Res_Err", sizes);
                    if (!ReferenceEquals(body, response.Data) || !ReferenceEquals(error, response.Err))
                    {
                        response.Rewrite(body, error, response.Headers);
                    }
                }
                if (sizes.Count > 0)
                {
                    access.Metadata["Truncated"] = sizes;
                }
            }
            return new(ProcessorResult.Continue);
        }

        private string? Cut(string? value, string name, JObject sizes)
        {
            if (value == null || value.Length <= _max)
            {
                return value;
            }
            sizes[name] = value.Length;
            return AuditTruncation.Prefix(value, _max);
        }
    }
}
