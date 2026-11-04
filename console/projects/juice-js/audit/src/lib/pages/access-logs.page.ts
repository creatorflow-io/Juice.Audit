import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { AuditApiService } from '../core/api.service';
import { AuditContextService } from '../core/context.service';
import { problemText } from '../shared/format';
import { cursorList } from '../shared/paging';
import { AccessLogTable, AuditContextBar } from '../shared/ui';

/**
 * The access records in the window, newest first: who did what, with what outcome and how long it took. The filters
 * are in the URL, so a filtered list is a link to share.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatInputModule, MatProgressBarModule, MatSelectModule,
    AuditContextBar, AccessLogTable],
  template: `
    <ja-context-bar><h1>Access records</h1></ja-context-bar>
    <form class="ja-filter-form" (ngSubmit)="apply()">
      <mat-form-field subscriptSizing="dynamic" class="ja-field-sm"><mat-label>User</mat-label><input matInput name="user" [(ngModel)]="form().user"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-sm"><mat-label>Action</mat-label><input matInput name="action" [(ngModel)]="form().action"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-sm"><mat-label>Path</mat-label><input matInput name="path" [(ngModel)]="form().path"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-xs">
        <mat-label>Method</mat-label>
        <mat-select name="method" [(ngModel)]="form().method">
          <mat-option value="">Any</mat-option>
          @for (m of methods; track m) { <mat-option [value]="m">{{ m }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-xs">
        <mat-label>Status</mat-label>
        <mat-select name="status" [(ngModel)]="form().status">
          <mat-option value="">Any</mat-option>
          <mat-option value="error">Failed (4xx, 5xx)</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-sm"><mat-label>App</mat-label><input matInput name="app" [(ngModel)]="form().app"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-sm"><mat-label>Slower than (ms)</mat-label><input matInput type="number" min="0" name="minMs" [(ngModel)]="form().minMs"></mat-form-field>
      <mat-checkbox name="restricted" [(ngModel)]="form().restricted">Restricted only</mat-checkbox>
      <button mat-flat-button type="submit">Search</button>
      @if (filtered()) { <button mat-button type="button" (click)="clear()">Clear</button> }
    </form>

    @if (logs.first.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (logs.first.error()) { <p class="ja-error">{{ error() }}</p> }
    @if (logs.first.hasValue()) {
      @if (logs.items().length === 0) { <p class="ja-muted">No access records match in this window.</p> }
      @else {
        <ja-access-log-table [rows]="logs.items()" />
        <div class="ja-more">
          <span class="ja-muted">{{ logs.items().length }} record{{ logs.items().length === 1 ? '' : 's' }}{{ logs.hasMore() ? ', newest first' : '' }}</span>
          @if (logs.hasMore()) {
            <button mat-button (click)="logs.more()" [disabled]="logs.loadingMore()">{{ logs.loadingMore() ? 'Loading…' : 'Older records' }}</button>
          }
          @if (logs.moreError()) { <span class="ja-error">{{ moreError() }}</span> }
        </div>
      }
    }`,
})
export class AccessLogsPage {
  private readonly api = inject(AuditApiService);
  protected readonly ctx = inject(AuditContextService);

  readonly user = input<string>();
  readonly action = input<string>();
  readonly path = input<string>();
  readonly method = input<string>();
  readonly status = input<string>();
  readonly app = input<string>();
  readonly minMs = input<string>();
  readonly restricted = input<string>();
  readonly traceId = input<string>();

  protected readonly methods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'MESSAGE', 'REQUEST', 'NOTIFICATION'];
  protected readonly statuses = ['2xx', '3xx', '4xx', '5xx'];

  private readonly filters = computed(() => ({
    user: this.user() ?? '', action: this.action() ?? '', path: this.path() ?? '', method: this.method() ?? '',
    status: this.status() ?? '', app: this.app() ?? '', minMs: this.minMs() ?? '', restricted: this.restricted() === 'true',
    traceId: this.traceId() ?? '',
  }));
  /** The form, reset to the URL's filters whenever they change. */
  protected readonly form = linkedSignal(() => ({ ...this.filters() }));
  protected readonly filtered = computed(() => Object.values(this.filters()).some(v => v !== '' && v !== false));

  protected readonly logs = cursorList(
    () => ({ filters: this.filters(), range: this.ctx.range(), abs: this.ctx.absolute() }),
    p => ({ ...this.ctx.window(), ...p.filters, restricted: p.filters.restricted ? true : null, limit: 50 }),
    q => this.api.accessLogs(q));
  protected readonly error = computed(() => problemText(this.logs.first.error()));
  protected readonly moreError = computed(() => problemText(this.logs.moreError()));

  apply() {
    const f = this.form();
    this.ctx.filter({
      user: f.user.trim() || null, action: f.action.trim() || null, path: f.path.trim() || null, method: f.method || null,
      status: f.status || null, app: f.app.trim() || null, minMs: String(f.minMs ?? '').trim() || null,
      restricted: f.restricted ? 'true' : null, traceId: f.traceId.trim() || null,
    });
  }

  clear() {
    this.ctx.filter({ user: null, action: null, path: null, method: null, status: null, app: null, minMs: null, restricted: null, traceId: null });
  }
}
