using Grpc.Core;
using Grpc.Net.ClientFactory;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class AuditGrpcClientOptionsExtensions
    {
        /// <summary>The gRPC header that carries the key a collector checks (<c>Collector:Grpc:ApiKeys</c>).</summary>
        public const string ApiKeyHeader = "x-api-key";

        /// <summary>
        /// The trailer a collector's key check sets when it refuses a call, before the service runs: nothing of it was
        /// stored, so the client may send it again.
        /// </summary>
        public const string KeyRefusedTrailer = "juice-audit-refused";

        /// <summary>The value of <see cref="KeyRefusedTrailer"/>.</summary>
        public const string KeyRefusedValue = "api-key";

        /// <summary>The call was refused by the collector's key check (<see cref="KeyRefusedTrailer"/>).</summary>
        public static bool IsKeyRefusal(RpcException ex)
            => ex.StatusCode == StatusCode.Unauthenticated
               && ex.Trailers.GetValue(KeyRefusedTrailer) == KeyRefusedValue;

        /// <summary>What <see cref="IsValidApiKey"/> asks of a key, for error messages.</summary>
        public const string ApiKeyRule = "it must be printable ASCII with no spaces, as an HTTP header value is.";

        /// <summary>
        /// A key both sides can use: printable ASCII with no spaces, so the HTTP stack neither refuses nor trims it.
        /// </summary>
        public static bool IsValidApiKey(string? apiKey)
            => !string.IsNullOrEmpty(apiKey) && apiKey.All(c => c > ' ' && c < '\u007f');

        /// <summary>
        /// Sends <paramref name="apiKey"/> in the <see cref="ApiKeyHeader"/> header of every call to the audit
        /// collector, for one that requires a key. <c>AddAuditPipeline</c> calls it with <c>Audit:Grpc:ApiKey</c>;
        /// with <c>ConfigureAuditGrpcClient</c>, call it in the client options:
        /// <code>services.ConfigureAuditGrpcClient(o =&gt; o.AddAuditApiKey(key));</code>
        /// The client options are applied when the client is first made, not at registration: an unusable key then
        /// fails the export worker, which logs it at Warning. With <c>AddGrpcAuditService</c> (the per-request client
        /// before 10.7) it fails each audited request's save instead, which is logged and doesn't fail the request.
        /// </summary>
        public static GrpcClientFactoryOptions AddAuditApiKey(this GrpcClientFactoryOptions options, string apiKey)
        {
            if (!IsValidApiKey(apiKey))
            {
                // Where it is called: at startup with Audit:Grpc:ApiKey, when the client is made with a delegate.
                throw new ArgumentException("The audit collector's API key is not usable: " + ApiKeyRule, nameof(apiKey));
            }
            options.CallOptionsActions.Add(call =>
            {
                // A new set: the call's own headers may be shared with other calls. A key set earlier (the option
                // applied twice) is replaced, not sent alongside: the collector reads only one.
                var headers = new Metadata();
                if (call.CallOptions.Headers != null)
                {
                    foreach (var entry in call.CallOptions.Headers)
                    {
                        if (!string.Equals(entry.Key, ApiKeyHeader, StringComparison.OrdinalIgnoreCase))
                        {
                            headers.Add(entry);
                        }
                    }
                }
                headers.Add(ApiKeyHeader, apiKey);
                call.CallOptions = call.CallOptions.WithHeaders(headers);
            });
            return options;
        }
    }
}
