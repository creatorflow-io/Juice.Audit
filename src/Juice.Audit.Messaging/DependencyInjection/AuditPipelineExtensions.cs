using Grpc.Net.ClientFactory;
using Juice.Audit.Messaging.Outbox;
using Juice.EF;
using Juice.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Where <see cref="AuditPipelineExtensions.AddAuditPipeline{TContext}"/> sends the audit records.</summary>
    public enum AuditMode
    {
        /// <summary>To a separate audit service: access records over gRPC, data changes through the app's broker.</summary>
        Remote,

        /// <summary>
        /// To the app's own audit database, with no broker and no separate service: access records by the in-process
        /// exporter, data changes through the outbox and the local transport.
        /// </summary>
        InProcess
    }

    /// <summary>The optional parts of <see cref="AuditPipelineExtensions.AddAuditPipeline{TContext}"/>.</summary>
    public sealed class AuditPipelineOptions
    {
        /// <summary>In-process only: the audit database (provider, schema, connection name).</summary>
        public Action<DbOptions>? ConfigureDb { get; set; }

        /// <summary>Remote only: the gRPC client, applied after <c>Audit:Grpc:Address</c>; it may set the address.</summary>
        public Action<GrpcClientFactoryOptions>? ConfigureGrpcClient { get; set; }

        /// <summary>Both modes: the outbox options, applied after <c>Audit:Outbox</c>.</summary>
        public Action<AuditOutboxOptions>? ConfigureOutbox { get; set; }

        /// <summary>
        /// In-process only: registers the outbox delivery processor of <c>TContext</c> for the <c>local</c> publisher.
        /// Set it to <c>false</c> when the app already calls <c>AddDeliveryProcessor&lt;TContext&gt;("local")</c>:
        /// two would deliver the same outbox twice over.
        /// </summary>
        public bool AddLocalDeliveryProcessor { get; set; } = true;
    }

    public static class AuditPipelineExtensions
    {
        /// <summary>The configuration key that picks the mode. It has no default.</summary>
        public const string ModeKey = "Audit:Mode";

        /// <summary>The collector's address in remote mode, unless <see cref="AuditPipelineOptions.ConfigureGrpcClient"/> sets it.</summary>
        public const string GrpcAddressKey = "Audit:Grpc:Address";

        /// <summary>
        /// Remote only: the key sent in the <c>x-api-key</c> header of every call, for a collector that requires one
        /// (<c>Collector:Grpc:ApiKeys</c>).
        /// </summary>
        public const string GrpcApiKeyKey = "Audit:Grpc:ApiKey";

        /// <summary>
        /// The whole audit pipeline for an app whose data changes run in <typeparamref name="TContext"/>'s transactions,
        /// in the mode <c>Audit:Mode</c> names (FR-022): <c>Remote</c> or <c>InProcess</c>. Switching needs no code
        /// change. The mode has no default: a missing or unknown value fails here, so a remote app can't fall into
        /// in-process mode by a missing setting.
        /// <para>The app still adds its outbox (<c>AddOutbox()</c>), <c>UseAudit()</c>, and the route of domain
        /// <c>juice.audit</c> in its publishing policy: to <c>local</c> in-process, to its broker remotely.</para>
        /// </summary>
        public static MessagingBuilder AddAuditPipeline<TContext>(this MessagingBuilder messaging,
            IConfiguration configuration, Action<AuditPipelineOptions>? configure = default)
            where TContext : DbContext
        {
            var options = new AuditPipelineOptions();
            configure?.Invoke(options);
            var services = messaging.Services;

            switch (ReadMode(configuration))
            {
                case AuditMode.InProcess:
                    services.ConfigureAuditDefault(configuration, options.ConfigureDb);
                    messaging.AddLocalPublisher();
                    messaging.AddLocalConsumer(c => c.SubscribeAuditDataChanges());
                    if (options.AddLocalDeliveryProcessor)
                    {
                        messaging.AddDelivery(d =>
                        {
                            d.AddDeliveryPolicies(_ => { });
                            d.AddDeliveryProcessor<TContext>("local");
                        });
                    }
                    break;

                case AuditMode.Remote:
                    var address = configuration[GrpcAddressKey];
                    var apiKey = configuration[GrpcApiKeyKey];
                    void Client(GrpcClientFactoryOptions o)
                    {
                        if (!string.IsNullOrWhiteSpace(address))
                        {
                            o.Address = new Uri(address);
                        }
                        // Empty means no key; anything else must be usable, blanks included (a secret left unfilled).
                        if (!string.IsNullOrEmpty(apiKey))
                        {
                            o.AddAuditApiKey(apiKey);
                        }
                        options.ConfigureGrpcClient?.Invoke(o);
                    }
                    // The client options are applied when the client is made; check the address now, at startup.
                    var check = new GrpcClientFactoryOptions();
                    Client(check);
                    if (check.Address == null)
                    {
                        throw new InvalidOperationException(
                            $"{ModeKey} is Remote but the audit collector has no address: set {GrpcAddressKey} " +
                            $"(e.g. https://audit:7285), or the Address in {nameof(AuditPipelineOptions.ConfigureGrpcClient)}.");
                    }
                    services.ConfigureAuditGrpcClient(Client);
                    break;
            }

            services.AddAuditOutbox<TContext>(options.ConfigureOutbox);
            return messaging;
        }

        /// <summary>The mode <c>Audit:Mode</c> names; throws when it is missing or unknown.</summary>
        public static AuditMode ReadMode(IConfiguration configuration)
        {
            var value = configuration[ModeKey];
            // By name only: "1" would otherwise parse as a mode.
            var name = Enum.GetNames(typeof(AuditMode))
                .FirstOrDefault(n => string.Equals(n, value?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                return Enum.Parse<AuditMode>(name);
            }
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(value)
                ? $"{ModeKey} is not set. Set it to {nameof(AuditMode.Remote)} (a separate audit service) or " +
                  $"{nameof(AuditMode.InProcess)} (this app's own audit database), in the \"Audit\" section."
                : $"{ModeKey} is '{value}', which is not a mode. Set it to {nameof(AuditMode.Remote)} or " +
                  $"{nameof(AuditMode.InProcess)}, in the \"Audit\" section.");
        }
    }
}
