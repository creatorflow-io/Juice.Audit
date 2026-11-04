import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { apiResource } from '../shared/resource';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { of } from 'rxjs';
import { AuditApiService } from '../core/api.service';
import { accessLogSummary } from '../core/models';
import { AuditContextService } from '../core/context.service';
import { AuditLinkPipe } from '../core/options';
import { KeysPipe, formatValue, problemText } from '../shared/format';
import { AccessLogTable, ActionBadge, AuditContextBar, ChangeDiff, TraceRef } from '../shared/ui';

/** One row change: its values before and after, and the request, message or job that made it. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, MatButtonModule, MatProgressBarModule, AuditLinkPipe, KeysPipe, AuditContextBar,
    AccessLogTable, ActionBadge, ChangeDiff, TraceRef],
  template: `
    <ja-context-bar [showRange]="false">
      <span class="ja-crumb"><a [routerLink]="['data-changes'] | jaLink" [queryParams]="ctx.keep()">Data changes</a> /</span>
      <h1>{{ title() }}</h1>
    </ja-context-bar>
    @if (change.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (change.error()) { <p class="ja-error">{{ error() }}</p> }
    @if (change.value(); as c) {
      <div class="ja-summary">
        <ja-action [action]="c.action" />
        <span>{{ c.dateTime | date: 'MMM d, y, HH:mm:ss.SSS' }}</span>
        <span>by <strong>{{ c.user || 'no user' }}</strong></span>
        @if (c.tenantId) { <span class="ja-muted">tenant {{ c.tenantId }}</span> }
        <span class="ja-mono">{{ c.keys | jaKeys }}</span>
        @if (c.database) { <span class="ja-muted">database {{ c.database }}</span> }
        <span class="ja-muted">trace <ja-trace-ref [traceId]="c.traceId" [short]="false" /></span>
      </div>
      <section class="ja-card">
        <div class="ja-row"><h2>Values</h2>
          @if (keyValue(); as k) {
            <a mat-button [routerLink]="['data-changes'] | jaLink" [queryParams]="ctx.keep({ table: c.table, key: k })">History of this row</a>
          }
        </div>
        <ja-change-diff [changes]="c.changes" />
      </section>
      <section class="ja-card">
        <h2>Made by</h2>
        @if (!c.traceId) { <p class="ja-muted">No trace id was recorded with this change.</p> }
        @if (trace.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
        @if (trace.value(); as t) {
          @if (t.accessLogs.length === 0) { <p class="ja-muted">No access record under this trace: the work that made it was not audited, or ran in another service.</p> }
          @else { <ja-access-log-table [rows]="accessLogs()" [compact]="true" /> }
          @if (t.dataChanges.length > 1) {
            <p><a [routerLink]="['traces', t.traceId] | jaLink" [queryParams]="ctx.keep()">{{ t.dataChanges.length - 1 }} more change{{ t.dataChanges.length === 2 ? '' : 's' }} in the same trace</a></p>
          }
        }
      </section>
    }`,
})
export class DataChangePage {
  private readonly api = inject(AuditApiService);
  protected readonly ctx = inject(AuditContextService);
  readonly id = input.required<string>();

  protected readonly change = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.dataChange(params) });
  protected readonly trace = apiResource({
    params: () => this.change.value()?.traceId ?? undefined,
    stream: ({ params }) => params ? this.api.trace(params) : of(undefined),
  });
  protected readonly title = computed(() => {
    const c = this.change.value();
    return c ? `${c.schema ? c.schema + '.' : ''}${c.table}` : 'Data change';
  });
  /** The row's key when it has a single one: the value a key search finds its history by. */
  protected readonly keyValue = computed(() => {
    const keys = this.change.value()?.keys;
    if (keys && typeof keys === 'object' && !Array.isArray(keys)) {
      const values = Object.values(keys);
      return values.length === 1 ? formatValue(values[0]) : null;
    }
    return null;
  });
  protected readonly accessLogs = computed(() => (this.trace.value()?.accessLogs ?? []).map(accessLogSummary));
  protected readonly error = computed(() => problemText(this.change.error()));
}
