import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { apiResource } from '../shared/resource';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuditApiService } from '../core/api.service';
import { accessLogSummary } from '../core/models';
import { AuditContextService } from '../core/context.service';
import { AuditLinkPipe, AuditLinks } from '../core/options';
import { KeysPipe, problemText } from '../shared/format';
import { AccessLogTable, ActionBadge, AuditContextBar, ChangeDiff } from '../shared/ui';

/**
 * Everything audited under one trace id, across services: the requests, messages and jobs that ran, and every row they
 * changed with its values, in order.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, MatProgressBarModule, AuditLinkPipe, KeysPipe, AuditContextBar, AccessLogTable,
    ActionBadge, ChangeDiff],
  template: `
    <ja-context-bar [showRange]="false"><h1>Trace <span class="ja-mono ja-h-id">{{ traceId() }}</span></h1></ja-context-bar>
    @if (external(); as x) {
      <p>@if ('href' in x) { <a [href]="x.href" target="_blank" rel="noopener">Open its spans and logs ↗</a> }
         @else { <a [routerLink]="x.route">Open its spans and logs ↗</a> }</p>
    }
    @if (trace.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (trace.error()) { <p class="ja-error">{{ error() }}</p> }
    @if (trace.value(); as t) {
      @if (t.accessLogs.length === 0 && t.dataChanges.length === 0) {
        <p class="ja-muted">Nothing was audited under this trace id.</p>
      }
      @if (t.truncated) { <p class="ja-notice">This trace has more records than the {{ t.accessLogs.length }} access records and {{ t.dataChanges.length }} changes shown.</p> }
      @if (t.accessLogs.length) {
        <section class="ja-card">
          <h2>Access records ({{ t.accessLogs.length }})</h2>
          <ja-access-log-table [rows]="accessLogs()" />
        </section>
      }
      @if (t.dataChanges.length) {
        <h2>Data changes ({{ t.dataChanges.length }})</h2>
        @for (c of t.dataChanges; track c.id) {
          <section class="ja-card">
            <div class="ja-row">
              <span class="ja-summary">
                <ja-action [action]="c.action" />
                <a class="ja-mono" [routerLink]="['data-changes', c.id] | jaLink" [queryParams]="ctx.keep()">{{ c.schema ? c.schema + '.' : '' }}{{ c.table }}</a>
                <span class="ja-mono ja-small">{{ c.keys | jaKeys }}</span>
              </span>
              <span class="ja-muted ja-small">{{ c.dateTime | date: 'HH:mm:ss.SSS' }} · {{ c.user || 'no user' }}</span>
            </div>
            <ja-change-diff [changes]="c.changes" />
          </section>
        }
      }
    }`,
})
export class TracePage {
  private readonly api = inject(AuditApiService);
  private readonly links = inject(AuditLinks);
  protected readonly ctx = inject(AuditContextService);
  readonly traceId = input.required<string>();

  protected readonly trace = apiResource({ params: () => this.traceId(), stream: ({ params }) => this.api.trace(params) });
  protected readonly external = computed(() => this.links.externalTrace(this.trace.value()?.traceId ?? this.traceId()));
  protected readonly accessLogs = computed(() => (this.trace.value()?.accessLogs ?? []).map(accessLogSummary));
  protected readonly error = computed(() => problemText(this.trace.error()));
}
