import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { apiResource } from '../shared/resource';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { of } from 'rxjs';
import { AuditApiService } from '../core/api.service';
import { AuditContextService } from '../core/context.service';
import { AuditLinkPipe } from '../core/options';
import { DurationPipe, PrettyJsonPipe, problemText } from '../shared/format';
import { AuditContextBar, DataChangeTable, KeyValueTable, StatusBadge, TraceRef } from '../shared/ui';

/** One access record: the request, its outcome, where it ran, its metadata and the data it changed. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, MatProgressBarModule, AuditLinkPipe, DurationPipe, PrettyJsonPipe, AuditContextBar,
    DataChangeTable, KeyValueTable, StatusBadge, TraceRef],
  template: `
    <ja-context-bar [showRange]="false">
      <span class="ja-crumb"><a [routerLink]="['access-logs'] | jaLink" [queryParams]="ctx.keep()">Access records</a> /</span>
      <h1>{{ log.value()?.action ?? 'Access record' }}</h1>
    </ja-context-bar>
    @if (log.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (log.error()) { <p class="ja-error">{{ error() }}</p> }
    @if (log.value(); as l) {
      <div class="ja-summary">
        <ja-status [status]="l.response?.status" />
        <span>{{ l.dateTime | date: 'MMM d, y, HH:mm:ss.SSS' }}</span>
        <span>{{ l.response?.elapsedMs | jaDuration }}</span>
        <span>by <strong>{{ l.user || 'no user' }}</strong></span>
        @if (l.tenantId) { <span class="ja-muted">tenant {{ l.tenantId }}</span> }
        @if (l.isRestricted) { <span class="ja-badge ja-tone-warn">restricted</span> }
        <span class="ja-muted">trace <ja-trace-ref [traceId]="l.traceId" [short]="false" /></span>
      </div>
      <div class="ja-grid-2">
        <section class="ja-card">
          <h2>Request</h2>
          @if (l.request; as r) {
            <p class="ja-mono">{{ r.method }} {{ r.scheme ? r.scheme + '://' : '' }}{{ r.host }}{{ r.path }}{{ r.query }}</p>
            <ja-kv [values]="{ 'Remote IP': r.remoteIp, 'Access zone': r.zone }" empty="" />
            @if (r.headers) { <h3>Headers</h3><pre class="ja-pre">{{ r.headers }}</pre> }
            @if (r.data) { <h3>Body</h3><pre class="ja-pre">{{ r.data | jaPretty }}</pre> }
          } @else { <p class="ja-muted">Not recorded.</p> }
        </section>
        <section class="ja-card">
          <h2>Response</h2>
          @if (l.response; as r) {
            @if (r.message) { <p>{{ r.message }}</p> }
            @if (r.error) { <h3>Error</h3><pre class="ja-pre ja-error">{{ r.error }}</pre> }
            @if (r.headers) { <h3>Headers</h3><pre class="ja-pre">{{ r.headers }}</pre> }
            @if (r.data) { <h3>Body</h3><pre class="ja-pre">{{ r.data | jaPretty }}</pre> }
            @if (!r.message && !r.error && !r.headers && !r.data) { <p class="ja-muted">Status and duration only.</p> }
          } @else { <p class="ja-muted">Not recorded.</p> }
        </section>
        <section class="ja-card">
          <h2>Server</h2>
          <ja-kv [values]="{ App: l.server?.app, Version: l.server?.appVersion, Machine: l.server?.machine, OS: l.server?.os }" empty="Not recorded." />
        </section>
        <section class="ja-card">
          <h2>Metadata</h2>
          <ja-kv [values]="l.metadata" />
        </section>
      </div>
      <section class="ja-card">
        <h2>Data changed ({{ l.dataChanges }})</h2>
        @if (l.dataChanges === 0) { <p class="ja-muted">No data changes under this trace.</p> }
        @else {
          @if (trace.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
          @if (trace.value(); as t) {
            <ja-data-change-table [rows]="t.dataChanges" [compact]="true" />
            @if (t.truncated) { <p class="ja-muted">The trace has more records than shown; <a [routerLink]="['traces', l.traceId] | jaLink">open the trace</a>.</p> }
          }
        }
      </section>
    }`,
})
export class AccessLogPage {
  private readonly api = inject(AuditApiService);
  protected readonly ctx = inject(AuditContextService);
  readonly id = input.required<string>();

  protected readonly log = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.accessLog(params) });
  protected readonly trace = apiResource({
    params: () => { const l = this.log.value(); return l?.traceId && l.dataChanges > 0 ? l.traceId : undefined; },
    stream: ({ params }) => params ? this.api.trace(params) : of(undefined),
  });
  protected readonly error = computed(() => problemText(this.log.error()));
}
