import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { AUDIT_RANGES, AuditContextService } from '../core/context.service';
import { AccessLogSummary, DataChangeSummary, JsonValue } from '../core/models';
import { AuditLinkPipe, AuditLinks } from '../core/options';
import { DurationPipe, KeysPipe, actionClass, formatValue, statusClass } from './format';

/**
 * The bar on top of every page: its title, the time window, and a lookup that opens everything audited under a trace
 * id.
 */
@Component({
  selector: 'ja-context-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatSelectModule, MatIconModule],
  template: `
    <div class="ja-context">
      <ng-content />
      <span class="ja-spacer"></span>
      @if (showRange()) {
        <mat-form-field subscriptSizing="dynamic" class="ja-field-sm">
          <mat-label>Time range</mat-label>
          <mat-select [value]="ctx.range()" (valueChange)="ctx.set({ range: $event })">
            @for (r of ranges; track r) { <mat-option [value]="r">Last {{ r }}</mat-option> }
            @if (ctx.range() === 'custom') { <mat-option value="custom" disabled>Custom window</mat-option> }
          </mat-select>
        </mat-form-field>
      }
      <form class="ja-lookup" (ngSubmit)="lookup()">
        <mat-icon>search</mat-icon>
        <input name="trace" [(ngModel)]="trace" placeholder="Trace ID" aria-label="Open the audit records of a trace">
      </form>
    </div>`,
})
export class AuditContextBar {
  protected readonly ctx = inject(AuditContextService);
  private readonly router = inject(Router);
  private readonly links = inject(AuditLinks);
  /** A page that is not about a window (one record, one trace) hides the range. */
  readonly showRange = input(true);
  protected readonly ranges = Object.keys(AUDIT_RANGES);
  protected trace = '';

  lookup() {
    const id = this.trace.trim();
    if (id) void this.router.navigate(this.links.to('traces', id), { queryParams: this.ctx.keep() });
  }
}

/** An HTTP status (or the outcome of a message or job) as a badge. */
@Component({
  selector: 'ja-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="ja-badge" [class]="tone()">{{ status() || '—' }}</span>`,
})
export class StatusBadge {
  readonly status = input<number | null | undefined>();
  protected readonly tone = computed(() => statusClass(this.status()));
}

/** Inserted, Modified or Deleted as a badge. */
@Component({
  selector: 'ja-action',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="ja-badge" [class]="tone()">{{ action() }}</span>`,
})
export class ActionBadge {
  readonly action = input.required<string>();
  protected readonly tone = computed(() => actionClass(this.action()));
}

/** A trace id: everything audited under it, and the host's tracing UI when it gave a link (provideJuiceAudit). */
@Component({
  selector: 'ja-trace-ref',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, AuditLinkPipe],
  template: `
    @if (traceId(); as id) {
      <a class="ja-mono" [routerLink]="['traces', id] | jaLink" [queryParams]="ctx.keep()" [attr.title]="'Everything audited under trace ' + id">{{ short() ? id.slice(0, 12) + '…' : id }}</a>
      @if (external(); as x) {
        @if ('href' in x) { <a class="ja-external" [href]="x.href" target="_blank" rel="noopener" title="Open the trace">spans ↗</a> }
        @else { <a class="ja-external" [routerLink]="x.route" title="Open the trace">spans ↗</a> }
      }
    } @else { <span class="ja-muted">—</span> }`,
})
export class TraceRef {
  protected readonly ctx = inject(AuditContextService);
  private readonly links = inject(AuditLinks);
  readonly traceId = input<string | null | undefined>();
  readonly short = input(true);
  protected readonly external = computed(() => this.links.externalTrace(this.traceId()));
}

/** Access records as a table, newest first; each opens its detail. */
@Component({
  selector: 'ja-access-log-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, AuditLinkPipe, DurationPipe, StatusBadge, TraceRef],
  template: `
    <table class="ja-table">
      <thead><tr><th>Time</th><th>User</th><th>Action</th><th>Status</th><th>Duration</th>@if (!compact()) { <th>App</th><th>Trace</th> }</tr></thead>
      <tbody>
        @for (r of rows(); track r.id) {
          <tr>
            <td class="ja-nowrap">{{ r.dateTime | date: 'MMM d, HH:mm:ss' }}</td>
            <td>{{ r.user || '—' }}@if (r.tenantId) { <div class="ja-muted ja-small">{{ r.tenantId }}</div> }</td>
            <td><a [routerLink]="['access-logs', r.id] | jaLink" [queryParams]="ctx.keep()">{{ r.action }}</a>
              @if (r.method || r.path) { <div class="ja-muted ja-small ja-mono">{{ r.method }} {{ r.path }}</div> }
              @if (r.isRestricted) { <span class="ja-badge ja-tone-warn">restricted</span> }</td>
            <td><ja-status [status]="r.status" /></td>
            <td class="ja-nowrap">@if (longest() > 0 && r.elapsedMs !== null) { <span class="ja-bar" [style.width.px]="60 * r.elapsedMs / longest()"></span> }{{ r.elapsedMs | jaDuration }}</td>
            @if (!compact()) {
              <td class="ja-muted">{{ r.app || '—' }}</td>
              <td class="ja-nowrap"><ja-trace-ref [traceId]="r.traceId" /></td>
            }
          </tr>
        }
      </tbody>
    </table>`,
})
export class AccessLogTable {
  protected readonly ctx = inject(AuditContextService);
  readonly rows = input.required<AccessLogSummary[]>();
  readonly compact = input(false);
  protected readonly longest = computed(() => Math.max(0, ...this.rows().map(r => r.elapsedMs ?? 0)));
}

/** Data changes as a table, newest first; each opens its before and after. */
@Component({
  selector: 'ja-data-change-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, AuditLinkPipe, KeysPipe, ActionBadge, TraceRef],
  template: `
    <table class="ja-table">
      <thead><tr><th>Time</th><th>User</th><th>Change</th><th>Table</th><th>Keys</th>@if (!compact()) { <th>Trace</th> }</tr></thead>
      <tbody>
        @for (c of rows(); track c.id) {
          <tr>
            <td class="ja-nowrap"><a [routerLink]="['data-changes', c.id] | jaLink" [queryParams]="ctx.keep()">{{ c.dateTime | date: 'MMM d, HH:mm:ss' }}</a></td>
            <td>{{ c.user || '—' }}@if (c.tenantId) { <div class="ja-muted ja-small">{{ c.tenantId }}</div> }</td>
            <td><ja-action [action]="c.action" /></td>
            <td class="ja-mono">{{ c.schema ? c.schema + '.' : '' }}{{ c.table }}@if (c.database) { <div class="ja-muted ja-small">{{ c.database }}</div> }</td>
            <td class="ja-mono ja-small">{{ c.keys | jaKeys }}</td>
            @if (!compact()) { <td class="ja-nowrap"><ja-trace-ref [traceId]="c.traceId" /></td> }
          </tr>
        }
      </tbody>
    </table>`,
})
export class DataChangeTable {
  protected readonly ctx = inject(AuditContextService);
  readonly rows = input.required<DataChangeSummary[]>();
  readonly compact = input(false);
}

interface DiffRow { field: string; before: string; after: string; changed: boolean; hasBefore: boolean; hasAfter: boolean; }

/**
 * A changed row's values side by side: before (OriginalValues) and after (CurrentValues), changed fields first. An
 * insert has no before and a delete no after.
 */
@Component({
  selector: 'ja-change-diff',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (rows().length === 0) { <p class="ja-muted">No values were recorded.</p> }
    @else {
      <table class="ja-table ja-diff">
        <thead><tr><th>Field</th><th>Before</th><th>After</th></tr></thead>
        <tbody>
          @for (r of rows(); track r.field) {
            <tr [class.ja-changed]="r.changed">
              <th class="ja-mono">{{ r.field }}</th>
              <td class="ja-mono" [class.ja-old]="r.changed && r.hasBefore">@if (r.hasBefore) { {{ r.before }} } @else { <span class="ja-muted">—</span> }</td>
              <td class="ja-mono" [class.ja-new]="r.changed && r.hasAfter">@if (r.hasAfter) { {{ r.after }} } @else { <span class="ja-muted">—</span> }</td>
            </tr>
          }
        </tbody>
      </table>
      @if (unchanged() > 0) { <p class="ja-muted ja-small">{{ unchanged() }} field{{ unchanged() === 1 ? '' : 's' }} unchanged, shown after the changed ones.</p> }
    }`,
})
export class ChangeDiff {
  readonly changes = input.required<JsonValue>();

  protected readonly rows = computed<DiffRow[]>(() => {
    const changes = asObject(this.changes());
    const before = asObject(changes['OriginalValues'] ?? changes['originalValues'] ?? null);
    const after = asObject(changes['CurrentValues'] ?? changes['currentValues'] ?? null);
    const fields = [...new Set([...Object.keys(before), ...Object.keys(after)])];
    const rows = fields.map(field => {
      const hasBefore = field in before;
      const hasAfter = field in after;
      const b = formatValue(before[field]);
      const a = formatValue(after[field]);
      return { field, before: b, after: a, hasBefore, hasAfter, changed: hasBefore !== hasAfter || b !== a };
    });
    return [...rows.filter(r => r.changed), ...rows.filter(r => !r.changed)];
  });
  protected readonly unchanged = computed(() => this.rows().filter(r => !r.changed).length);
}

function asObject(value: JsonValue | undefined): Record<string, JsonValue> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

/** Name/value pairs, sorted, empty ones left out. */
@Component({
  selector: 'ja-kv',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (entries().length === 0) { <p class="ja-muted">{{ empty() }}</p> }
    @else {
      <table class="ja-kv"><tbody>
        @for (e of entries(); track e[0]) { <tr><th>{{ e[0] }}</th><td>{{ e[1] }}</td></tr> }
      </tbody></table>
    }`,
})
export class KeyValueTable {
  readonly values = input<Record<string, string | number | boolean | null | undefined>>({});
  readonly empty = input('None');
  protected readonly entries = computed(() =>
    Object.entries(this.values())
      .filter((e): e is [string, string | number | boolean] => e[1] !== null && e[1] !== undefined && e[1] !== '')
      .sort((a, b) => a[0].localeCompare(b[0])));
}
