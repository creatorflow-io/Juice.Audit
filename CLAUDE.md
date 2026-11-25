# audit Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-11-19

## Active Technologies

- C# latest on `net6.0;net8.0;net9.0;net10.0` (tests `net8.0;net9.0;net10.0`) + Juice 10.15 (Juice.MediatR, Juice.EF, Juice.Messaging, Juice.Messaging.Outbox, Juice.Messaging.Local), gRPC 2.76, System.Threading.Channels (002-audit-collector-pipeline)
- `AuditDbContext` on PostgreSQL or SQL Server; tables unchanged (002-audit-collector-pipeline)

## Project Structure

```text
src/      Juice.Audit, .Api, .Api.Contracts, .AspNetCore, .EF, .EF.PostgreSQL, .EF.SqlServer, .Messaging
          Juice.Audit.Collector (the collector host and its Dockerfile; net10.0, not packed)
deploy/   collector/docker-compose.yml: collector + PostgreSQL + RabbitMQ, for CollectorContainerTests
test/     Juice.Audit.Tests, .Scopes.Tests, .Tests.Host
specs/    speckit features
console/  @juice-js/audit Angular pages
```

## Commands

```powershell
dotnet build Juice.Audit.sln
# Build through the solution: OutputPath uses $(SolutionDir) and $(Configuration), which a project-only
# build leaves empty. Testing a project then needs both, or it runs stale binaries from build/bin/<project>.
dotnet test test/Juice.Audit.Scopes.Tests -f net10.0 -c Debug -p:SolutionDir=$PWD/
dotnet test test/Juice.Audit.Tests -f net10.0 -c Debug -p:SolutionDir=$PWD/   # infrastructure tests are IgnoreOnCIFact
```

## Code Style

C#: follow the existing code and `.editorconfig`; constitution in `.specify/memory/constitution.md`.

## Recent Changes

- 002-audit-collector-pipeline: outbox for data changes, in-memory batch exporter for access records, collector with processors and idempotent writes

<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
