<!--
SYNC IMPACT REPORT
==================
Version change: 1.0.0 → 1.0.1
Bump type: PATCH — wording matched to the code (found by /speckit.analyze on 002-audit-collector-pipeline):
  - Principle II layering: Api.Contracts (generated gRPC types) sits below Juice.Audit, which
    already references it for the gRPC client.
  - Technology Constraints: integration events use the Juice message serializer (Newtonsoft),
    not System.Text.Json.

History:
  1.0.0 (2026-11-19): initial — adapted from the Juice core constitution 1.1.0 for the Juice.Audit repository.

Changes from Juice core 1.1.0:
  - Principles I–V kept; paths changed to this repo's layout (src/, test/) and the
    layering to the Juice service convention.
  - Added Principle VI (Audit Never Breaks the Host), stating rules the code already
    follows (AuditRecorder never throws; W3C trace id on every record).
  - Technology Constraints grounded in this repo's Directory.Build.props (gRPC, Juice
    package versions, Newtonsoft.Json for record payloads).

Templates status:
  ✅ .specify/templates/* — copied unchanged from Juice core; generic, compatible.

Follow-up TODOs: none.
-->

# Juice.Audit Constitution

## Core Principles

### I. Lightweight & Dual-Architecture

Juice.Audit MUST work both inside a single app and as a separate audit service, from the same packages, without
rewrites in the consuming app.

- Every deployment mode (in-process, remote over gRPC and broker) MUST be reachable by configuration, not by code
  changes in the app.
- Inter-component dependencies MUST go through abstractions (`IAuditService`, repositories, exporters), not concrete
  classes.
- New abstractions MUST be justified by at least one concrete use — YAGNI applies.

**Rationale**: Apps start with auditing in-process and move to a central audit service as they grow. That move must
not require changing the app.

### II. Library-First Composability

Every feature MUST ship as an independently publishable NuGet package under `src/`, with tests under `test/`.

- Packages MUST follow the layering
  `Api.Contracts (proto) → Abstractions/Domain (Juice.Audit) → EF → EF.PostgreSQL / EF.SqlServer → Api → AspNetCore / Messaging`.
  `Api.Contracts` holds only generated gRPC types and sits at the bottom, so that the gRPC client in `Juice.Audit`
  can use it.
- No package may depend on a layer above it; no circular dependencies.
- Optional integrations (Messaging, Measurement stores) MUST live in their own package or be resolved as optional
  services, so an app pays only for what it uses.

**Rationale**: Apps include only the audit pieces they need, keeping their dependency footprint small.

### III. Domain-Driven Design + CQRS

Audit records MUST be modeled as Juice aggregates, and writes MUST go through commands dispatched by
`Juice.MediatR.IMediator`; the MediatR NuGet package MUST NOT be used for dispatch.

- Pipeline behaviors MUST declare `int Order` (ascending = outermost). Audit scope behaviors are outermost
  (`int.MinValue`) so the work of every other behavior is inside the scope.
- The host's `TransactionBehavior` sequence (SaveChanges → DispatchDomainEvents → SaveOutbox → Commit) MUST NOT be
  changed or reordered by audit code; audit joins it through notifications and the outbox.

**Rationale**: A consistent model keeps audit writes testable and keeps audit from interfering with the host's
transactions.

### IV. Reliable Messaging via Outbox Pattern

A guarantee that an audit record is saved with a business change MUST come from the host's transactional outbox,
never from a write made after the commit.

- Integration events MUST be written through `IOutboxService` inside the host transaction and delivered by
  `DeliveryHostedService<TContext>`; direct broker publishing from audit code is forbidden.
- Routing MUST go through `IMessagePublishingPolicy`; no hard-coded exchange or queue names.
- `MessageContext` MUST be initialized at every entry point (middleware, consumers, tests via
  `[InitializeMessageContext]`).
- Consumers of audit events MUST be idempotent (inbox check and idempotent insert by record id).

**Rationale**: An audit trail is only trustworthy if a committed change can't be missing from it.

### V. Multi-Tenancy First

Every audit record MUST carry its tenant, and reads MUST be isolated by tenant at the `AuditDbContext` level.

- Tenant resolution MUST use Juice's tenant abstractions (`ITenantAccessor`, Finbuckle via Juice.EF.MultiTenant).
- A tenant sees only its own records; the root tenant sees all. API-layer filtering alone does not satisfy this.
- A batch that mixes tenants MUST store each record under its own tenant.

**Rationale**: Audit data is among the most sensitive data a tenant has; leakage between tenants is unacceptable.

### VI. Audit Never Breaks the Host

Auditing MUST NOT make a request, message or job fail, hang, or change its result.

- Audit code on the request path MUST NOT throw to the host; failures are logged as warnings
  (`AuditRecorder` pattern).
- Audit code MUST NOT wait on the network without a deadline.
- Any loss of an audit record MUST be observable: counted in a metric or logged, never silent.
- Every record MUST carry the W3C trace id of the work that produced it, so it can be followed in OpenTelemetry back
  ends.

**Rationale**: Auditing is a side concern of every app that uses it; it must never become the reason one fails.

## Technology Constraints

- **Language / Runtime**: C# (`LangVersion=latest`). Apps and libraries target `net6.0;net8.0;net9.0;net10.0`
  (`AppTargetFramework`); tests target `net8.0;net9.0;net10.0` (`AppTestTargetFramework`). The authoritative matrix
  lives in `Directory.Build.props`.
- **ORM**: EF Core, version-matched per target (`EFVersion`). Schema changes MUST ship migrations for both
  PostgreSQL (`Juice.Audit.EF.PostgreSQL`) and SQL Server (`Juice.Audit.EF.SqlServer`).
- **Juice packages**: `JuiceVersion`, `JuiceMessagingVersion`, `JuiceMeasurement` in `Directory.Build.props`; local
  builds use `-alpha.*` references.
- **Remote transport**: gRPC (`GrpcVersion`, `ProtobufVersion`); contracts in `Juice.Audit.Api.Contracts/Protos`.
  Proto changes MUST be backward compatible (add fields and RPCs; never renumber or remove).
- **Messaging**: Juice.Messaging (outbox, `IIntegrationEventPipelineBehavior`); any broker the host configures.
- **Multi-tenancy**: Finbuckle.MultiTenant (`FinbuckleVersion`) through Juice.
- **Serialization**: Newtonsoft.Json for audit record payloads (metadata, key values, changes). Integration events
  use the Juice message serializer (Newtonsoft, `TypeNameHandling.All`, known-types binder); audit events MUST carry
  only strings and primitives so the payload doesn't depend on type binding. Do not mix serializers within the same
  boundary.
- **Observability**: `System.Activity` (W3C) for trace ids; `System.Diagnostics.Metrics` for counters.
- **Testing**: xUnit v3 (`XUnitVersion`).

## Development Workflow & Quality Gates

- Infrastructure-dependent tests (database, broker, gRPC host) MUST be guarded with `IgnoreOnCIFact`.
- Messaging tests MUST apply `[InitializeMessageContext]`.
- Breaking changes to a public API MUST increment the MAJOR `VersionPrefix` and be documented in the release notes;
  changes to a delivery guarantee MUST be documented in the README even when the API is unchanged.
- Each new `src/` package MUST have a `test/` project or be covered by an existing one.
- All PRs MUST include the Constitution Check from `plan.md` against the six principles above.
- Any deviation (a write after commit instead of the outbox, a network call without a deadline, a concrete
  dependency across packages) MUST be justified in the PR with the simpler alternatives considered.

## Governance

This constitution governs the Juice.Audit repository and follows the Juice core constitution; where they conflict on
framework-wide matters (MediatR, outbox, multi-tenancy), the core constitution wins.

**Amendment procedure**: propose the change in a PR with a `constitution:` commit prefix, state what changes and why,
update the version and dates below, and propagate to the templates in the same PR.

**Versioning policy** (semantic): MAJOR for removing or redefining a principle; MINOR for a new principle or section;
PATCH for wording.

Runtime development guidance for the Juice framework lives in `Juice/.claude/` (architecture.md, messaging-outbox.md,
domain-patterns.md, di-patterns.md, extensions.md).

**Version**: 1.0.1 | **Ratified**: 2026-11-19 | **Last Amended**: 2026-11-20
