using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Juice.Audit.Collector.Host
{
    /// <summary>
    /// Refuses gRPC calls without one of <c>Collector:Grpc:ApiKeys</c> in the <c>x-api-key</c> header
    /// (<see cref="StatusCode.Unauthenticated"/>). Apps send it with <c>Audit:Grpc:ApiKey</c>.
    /// </summary>
    internal sealed class ApiKeyInterceptor : Interceptor
    {
        public const string Header = Microsoft.Extensions.DependencyInjection.AuditGrpcClientOptionsExtensions.ApiKeyHeader;

        private readonly byte[][] _keys;

        public ApiKeyInterceptor(CollectorSettings settings)
        {
            _keys = settings.Grpc.ApiKeys.Select(k => Encoding.UTF8.GetBytes(k)).ToArray();
        }

        public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request,
            ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            Check(context);
            return continuation(request, context);
        }

        public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
            ClientStreamingServerMethod<TRequest, TResponse> continuation)
        {
            Check(context);
            return continuation(requestStream, context);
        }

        public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request,
            IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
            ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            Check(context);
            return continuation(request, responseStream, context);
        }

        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            Check(context);
            return continuation(requestStream, responseStream, context);
        }

        private void Check(ServerCallContext context)
        {
            // Registered only with keys (Program.cs). With none it refuses every call rather than letting them in.
            var sent = context.RequestHeaders.GetValue(Header);
            if (sent != null)
            {
                var bytes = Encoding.UTF8.GetBytes(sent);
                // Every key is compared, in constant time, so the time taken tells nothing about the keys.
                var match = false;
                foreach (var key in _keys)
                {
                    match |= CryptographicOperations.FixedTimeEquals(bytes, key);
                }
                if (match)
                {
                    return;
                }
            }
            // Tagged, so the app knows nothing was stored and sends the record again once its key is fixed.
            throw new RpcException(new Status(StatusCode.Unauthenticated,
                    sent == null ? $"The {Header} header is missing." : $"The {Header} header is not a key of this collector."),
                new Metadata
                {
                    { AuditGrpcClientOptionsExtensions.KeyRefusedTrailer, AuditGrpcClientOptionsExtensions.KeyRefusedValue }
                });
        }
    }
}
