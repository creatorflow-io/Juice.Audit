namespace Juice.Audit.Collector.Host
{
    /// <summary>
    /// The collector's own settings (section <c>Collector</c>), read and checked once at startup: a setting that
    /// can't work stops the collector with a message naming the key, rather than failing on the first record.
    /// </summary>
    internal sealed class CollectorSettings
    {
        public const string Section = "Collector";

        public DatabaseSettings Database { get; set; } = new();
        public RabbitMQSettings RabbitMQ { get; set; } = new();
        public GrpcSettings Grpc { get; set; } = new();
        public ReadApiSettings ReadApi { get; set; } = new();

        /// <summary>A broker is configured: the collector consumes the apps' data changes.</summary>
        public bool HasBroker => !string.IsNullOrWhiteSpace(RabbitMQ.Host);

        public static CollectorSettings Read(IConfiguration configuration)
        {
            var settings = new CollectorSettings();
            configuration.GetSection(Section).Bind(settings);
            var errors = settings.Validate(configuration).ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("The audit collector can't start:" + Environment.NewLine
                    + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
            }
            return settings;
        }

        internal IEnumerable<string> Validate(IConfiguration configuration)
        {
            var provider = Database.NormalizedProvider;
            if (provider == null)
            {
                yield return string.IsNullOrWhiteSpace(Database.Provider)
                    ? "Collector:Database:Provider is not set. Set it to PostgreSQL or SqlServer."
                    : $"Collector:Database:Provider is '{Database.Provider}'. Set it to PostgreSQL or SqlServer.";
            }
            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(Database.ConnectionName)))
            {
                yield return $"ConnectionStrings:{Database.ConnectionName} is not set: the audit database's connection "
                    + "string (Collector:Database:ConnectionName names another).";
            }
            if (HasBroker)
            {
                if (string.IsNullOrWhiteSpace(RabbitMQ.Exchange))
                {
                    yield return "Collector:RabbitMQ:Exchange is empty: the exchange the apps publish juice.audit to.";
                }
                if (string.IsNullOrWhiteSpace(RabbitMQ.Queue))
                {
                    yield return "Collector:RabbitMQ:Queue is empty: the queue the collector reads.";
                }
            }
            // A key the apps can't send as a header would refuse every call. Counted, not named by index: the bound
            // array's positions aren't the configuration's keys when those have gaps.
            var unusable = Grpc.ApiKeys.Count(k => !AuditGrpcClientOptionsExtensions.IsValidApiKey(k));
            if (unusable > 0)
            {
                yield return $"Collector:Grpc:ApiKeys has {unusable} unusable key(s): each "
                    + AuditGrpcClientOptionsExtensions.ApiKeyRule["it ".Length..];
            }
            if (ReadApi.Enabled && ReadApi.AllowAnonymous && ReadApi.RequiredScopes.Length > 0)
            {
                yield return "Collector:ReadApi:RequiredScopes is set but Collector:ReadApi:AllowAnonymous is true, which "
                    + "lets everyone in: turn AllowAnonymous off, or clear RequiredScopes.";
            }
            if (ReadApi.Enabled && !ReadApi.AllowAnonymous)
            {
                if (string.IsNullOrWhiteSpace(ReadApi.Jwt.Authority))
                {
                    yield return "Collector:ReadApi is enabled with no sign-in: set Collector:ReadApi:Jwt:Authority, or "
                        + "Collector:ReadApi:AllowAnonymous to true where the network already limits who can reach it.";
                }
                else if (string.IsNullOrWhiteSpace(ReadApi.Jwt.Audience))
                {
                    // Without it a token issued for any other API of the same provider is accepted. Scope names alone
                    // don't tell APIs apart (Entra ID: "access_as_user" on every API that defines it).
                    yield return "Collector:ReadApi:Jwt:Audience is not set: set it to the audience of the audit "
                        + "collector's tokens, so tokens issued for other APIs are refused.";
                }
            }
        }
    }

    internal sealed class DatabaseSettings
    {
        /// <summary><c>PostgreSQL</c> or <c>SqlServer</c>. No default: the image serves both.</summary>
        public string? Provider { get; set; }

        /// <summary>The schema of the audit tables.</summary>
        public string Schema { get; set; } = "Audit";

        /// <summary>The name of the connection string, under <c>ConnectionStrings</c>.</summary>
        public string ConnectionName { get; set; } = "Audit";

        /// <summary>
        /// Brings the database to the current schema at startup. Turn it off when several collectors run, and migrate
        /// once with <c>--migrate</c> (an init container or a job) instead.
        /// </summary>
        public bool Migrate { get; set; } = true;

        public string? NormalizedProvider => Provider?.Trim().ToLowerInvariant() switch
        {
            "postgresql" or "postgres" or "npgsql" => "PostgreSQL",
            "sqlserver" or "mssql" => "SqlServer",
            _ => null
        };
    }

    internal sealed class RabbitMQSettings
    {
        /// <summary>The broker's host. Without it the collector takes access records over gRPC only.</summary>
        public string? Host { get; set; }
        public int Port { get; set; } = 5672;
        public string? UserName { get; set; }
        public string? Password { get; set; }
        public string? VirtualHost { get; set; }

        /// <summary>The topic exchange the apps' outboxes publish domain <c>juice.audit</c> to.</summary>
        public string Exchange { get; set; } = "juice.audit";

        /// <summary>The queue the collector declares, binds to <see cref="Exchange"/> and reads.</summary>
        public string Queue { get; set; } = "juice.audit";
    }

    internal sealed class GrpcSettings
    {
        /// <summary>
        /// Keys an app must send in the <c>x-api-key</c> header of its gRPC calls. Several, so a key can be replaced
        /// without downtime. None: the gRPC service is open to anyone who can reach it.
        /// </summary>
        public string[] ApiKeys { get; set; } = Array.Empty<string>();
    }

    internal sealed class ReadApiSettings
    {
        /// <summary>The authorization policy built from <see cref="RequiredScopes"/>.</summary>
        public const string PolicyName = "audit-readers";

        /// <summary>Maps the read API (<c>MapJuiceAudit</c>) that the <c>@juice-js/audit</c> pages use.</summary>
        public bool Enabled { get; set; }

        public string Path { get; set; } = "/api/audit";

        /// <summary>
        /// The token must grant one of these scopes (its <c>scope</c> or <c>scp</c> claim). None: any signed-in caller.
        /// </summary>
        public string[] RequiredScopes { get; set; } = Array.Empty<string>();

        /// <summary>No sign-in at all. Only where the network already limits who can reach the collector.</summary>
        public bool AllowAnonymous { get; set; }

        /// <summary>The bearer token settings: <c>Authority</c>, <c>Audience</c>, <c>RequireHttpsMetadata</c>, …</summary>
        public JwtSettings Jwt { get; set; } = new();
    }

    internal sealed class JwtSettings
    {
        public string? Authority { get; set; }
        public string? Audience { get; set; }
        public string? MetadataAddress { get; set; }
        public bool RequireHttpsMetadata { get; set; } = true;
    }
}
