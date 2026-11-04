import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { apiResource } from '../shared/resource';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuditApiService } from '../core/api.service';
import { AuditContextService } from '../core/context.service';
import { AuditCount } from '../core/models';
import { AuditLinkPipe } from '../core/options';
import { problemText } from '../shared/format';
import { AccessLogTable, AuditContextBar } from '../shared/ui';

/**
 * The window at a glance: how much was audited, how much failed, who and which apps, which tables changed; then the
 * latest failures and the slowest requests. Every count opens the list it counts.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatProgressBarModule, AuditLinkPipe, AuditContextBar, AccessLogTable],
  template: `
    <ja-context-bar><h1>Audit</h1></ja-context-bar>
    @if (overview.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (overview.error()) { <p class="ja-error">{{ error() }}</p> }
    @if (overview.value(); as o) {
      <div class="ja-tiles">
        <a class="ja-card ja-tile" [routerLink]="['access-logs'] | jaLink" [queryParams]="ctx.keep()"><span class="ja-muted">Access records</span><strong>{{ o.requests }}</strong></a>
        <a class="ja-card ja-tile" [routerLink]="['access-logs'] | jaLink" [queryParams]="ctx.keep({ status: '4xx' })"><span class="ja-muted">Client errors (4xx)</span><strong [class.ja-warn]="o.clientErrors">{{ o.clientErrors }}</strong></a>
        <a class="ja-card ja-tile" [routerLink]="['access-logs'] | jaLink" [queryParams]="ctx.keep({ status: '5xx' })"><span class="ja-muted">Server errors (5xx)</span><strong [class.ja-bad]="o.serverErrors">{{ o.serverErrors }}</strong></a>
        <a class="ja-card ja-tile" [routerLink]="['access-logs'] | jaLink" [queryParams]="ctx.keep({ restricted: 'true' })"><span class="ja-muted">Restricted</span><strong>{{ o.restricted }}</strong></a>
        <a class="ja-card ja-tile" [routerLink]="['data-changes'] | jaLink" [queryParams]="ctx.keep()"><span class="ja-muted">Data changes</span><strong>{{ o.dataChanges }}</strong></a>
      </div>

      <div class="ja-grid-3">
        <section class="ja-card">
          <h2>Status</h2>
          @for (s of o.statuses; track s.name) {
            <div class="ja-count">
              <a [routerLink]="['access-logs'] | jaLink" [queryParams]="ctx.keep(s.name === 'other' ? {} : { status: s.name })">{{ s.name }}</a>
              <span class="ja-count-bar"><i [class]="s.name.startsWith('5') ? 'ja-fill-bad' : s.name.startsWith('4') ? 'ja-fill-warn' : 'ja-fill-ok'" [style.width.%]="share(s, o.statuses)"></i></span>
              <span>{{ s.count }}</span>
            </div>
          } @empty { <p class="ja-muted">No access records in this window.</p> }
        </section>
        <section class="ja-card">
          <h2>Apps</h2>
          @for (a of o.apps; track a.name) {
            <div class="ja-count">
              <a [routerLink]="['access-logs'] | jaLink" [queryParams]="ctx.keep({ app: a.name || null })">{{ a.name || '(none)' }}</a>
              <span class="ja-count-bar"><i class="ja-fill-ok" [style.width.%]="share(a, o.apps)"></i></span>
              <span>{{ a.count }}@if (a.errors) { <span class="ja-bad ja-small"> · {{ a.errors }} failed</span> }</span>
            </div>
          } @empty { <p class="ja-muted">None.</p> }
        </section>
        <section class="ja-card">
          <h2>Users</h2>
          @for (u of o.users; track u.name) {
            <div class="ja-count">
              @if (u.name) { <a [routerLink]="['access-logs'] | jaLink" [queryParams]="ctx.keep({ user: u.name })">{{ u.name }}</a> }
              @else { <span class="ja-muted" title="Messages and background jobs run without a user">(no user)</span> }
              <span class="ja-count-bar"><i class="ja-fill-ok" [style.width.%]="share(u, o.users)"></i></span>
              <span>{{ u.count }}@if (u.errors) { <span class="ja-bad ja-small"> · {{ u.errors }} failed</span> }</span>
            </div>
          } @empty { <p class="ja-muted">None.</p> }
        </section>
      </div>

      <div class="ja-grid-2">
        <section class="ja-card">
          <h2>Changed tables</h2>
          @for (t of o.tables; track t.name) {
            <div class="ja-count">
              <a class="ja-mono" [routerLink]="['data-changes'] | jaLink" [queryParams]="ctx.keep({ table: tableOf(t.name) })">{{ t.name }}</a>
              <span class="ja-count-bar"><i class="ja-fill-info" [style.width.%]="share(t, o.tables)"></i></span>
              <span>{{ t.count }}</span>
            </div>
          } @empty { <p class="ja-muted">No data changed in this window.</p> }
        </section>
        <section class="ja-card">
          <h2>Changes</h2>
          @for (a of o.changeActions; track a.name) {
            <div class="ja-count">
              <a [routerLink]="['data-changes'] | jaLink" [queryParams]="ctx.keep({ action: a.name })">{{ a.name }}</a>
              <span class="ja-count-bar"><i class="ja-fill-info" [style.width.%]="share(a, o.changeActions)"></i></span>
              <span>{{ a.count }}</span>
            </div>
          } @empty { <p class="ja-muted">None.</p> }
        </section>
      </div>

      <section class="ja-card">
        <div class="ja-row"><h2>Latest failures</h2><a [routerLink]="['access-logs'] | jaLink" [queryParams]="ctx.keep({ status: 'error' })">All failures</a></div>
        @if (o.recentErrors.length) { <ja-access-log-table [rows]="o.recentErrors" /> }
        @else { <p class="ja-muted">No failed requests in this window.</p> }
      </section>
      <section class="ja-card">
        <h2>Slowest</h2>
        @if (o.slowest.length) { <ja-access-log-table [rows]="o.slowest" /> }
        @else { <p class="ja-muted">No timed requests in this window.</p> }
      </section>
    }`,
})
export class OverviewPage {
  private readonly api = inject(AuditApiService);
  protected readonly ctx = inject(AuditContextService);

  protected readonly overview = apiResource({
    params: () => ({ range: this.ctx.range(), abs: this.ctx.absolute() }),
    stream: () => this.api.overview(this.ctx.window()),
  });
  protected readonly error = computed(() => problemText(this.overview.error()));

  share(item: AuditCount, all: AuditCount[]) { return 100 * item.count / Math.max(1, ...all.map(a => a.count)); }

  /** `schema.table` → `table`: the list filters on the table name. */
  tableOf(name: string) { return name.includes('.') ? name.slice(name.lastIndexOf('.') + 1) : name; }
}
