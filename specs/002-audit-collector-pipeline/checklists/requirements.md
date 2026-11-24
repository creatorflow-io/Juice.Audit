# Specification Quality Checklist: Audit Collector Pipeline (app → collector → db)

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-11-19
**Feature**: [spec.md](../spec.md)

## Content Quality

- [ ] No implementation details (languages, frameworks, APIs)
- [x] Focused on user value and business needs
- [ ] Written for non-technical stakeholders
- [x] All mandatory sections completed

## Requirement Completeness

- [x] No [NEEDS CLARIFICATION] markers remain
- [x] Requirements are testable and unambiguous
- [x] Success criteria are measurable
- [ ] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows
- [x] Feature meets measurable outcomes defined in Success Criteria
- [ ] No implementation details leak into specification

## Notes

- The unchecked items are deliberate. The feature changes how a framework library delivers its records, so the spec
  names the Juice components it builds on (`TransactionBehavior`, the outbox, `IOutboxContext`, the local transport,
  gRPC, `System.Diagnostics.Metrics`). Without them, the guarantees in FR-001, FR-014 and FR-025 can't be stated
  precisely.
- To settle in `/speckit.plan`:
  - Which outbox the audit handler writes to when an app has several outbox contexts. The data change event carries
    the `TransactionId`; the plan must map it to the `IOutboxService<TContext>` of that transaction.
  - Default exporter values (queue capacity, batch size and age, deadline, retries).
  - The idempotent insert on each provider (`ON CONFLICT DO NOTHING` / `MERGE` or a unique index with
    ignore-duplicates) and the migration for existing rows that have server-generated ids.
  - How the message size limit for splitting events (FR-002) is set for each transport.
- Ready for `/speckit.plan`.
- 2026-11-20, after `/speckit.analyze`: the idempotent insert needs no migration (research R8, ids set in the app);
  FR-022 now asks for one call with the mode taken from `Audit:Mode`; the old-server edge case describes the unary
  fallback (R10); US4 scenario 1 and SC-003 count round trips per EF batch, not one per table.
