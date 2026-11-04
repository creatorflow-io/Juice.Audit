import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { AuditApiService } from '../core/api.service';
import { AuditContextService } from '../core/context.service';
import { problemText } from '../shared/format';
import { cursorList } from '../shared/paging';
import { AuditContextBar, DataChangeTable } from '../shared/ui';

/**
 * The row changes in the window, newest first: which table, which row (its keys), who and in which trace. Searching
 * by key finds a row's history.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatInputModule, MatProgressBarModule, MatSelectModule, AuditContextBar, DataChangeTable],
  template: `
    <ja-context-bar><h1>Data changes</h1></ja-context-bar>
    <form class="ja-filter-form" (ngSubmit)="apply()">
      <mat-form-field subscriptSizing="dynamic" class="ja-field-sm"><mat-label>Table</mat-label><input matInput name="table" [(ngModel)]="form().table"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-sm"><mat-label>Key value</mat-label><input matInput name="key" [(ngModel)]="form().key" placeholder="e.g. an order id"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-xs">
        <mat-label>Change</mat-label>
        <mat-select name="action" [(ngModel)]="form().action">
          <mat-option value="">Any</mat-option>
          @for (a of actions; track a) { <mat-option [value]="a">{{ a }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-sm"><mat-label>User</mat-label><input matInput name="user" [(ngModel)]="form().user"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="ja-field-xs"><mat-label>Schema</mat-label><input matInput name="schema" [(ngModel)]="form().schema"></mat-form-field>
      <button mat-flat-button type="submit">Search</button>
      @if (filtered()) { <button mat-button type="button" (click)="clear()">Clear</button> }
    </form>

    @if (changes.first.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (changes.first.error()) { <p class="ja-error">{{ error() }}</p> }
    @if (changes.first.hasValue()) {
      @if (changes.items().length === 0) { <p class="ja-muted">No data changes match in this window.</p> }
      @else {
        <ja-data-change-table [rows]="changes.items()" />
        <div class="ja-more">
          <span class="ja-muted">{{ changes.items().length }} change{{ changes.items().length === 1 ? '' : 's' }}{{ changes.hasMore() ? ', newest first' : '' }}</span>
          @if (changes.hasMore()) {
            <button mat-button (click)="changes.more()" [disabled]="changes.loadingMore()">{{ changes.loadingMore() ? 'Loading…' : 'Older changes' }}</button>
          }
          @if (changes.moreError()) { <span class="ja-error">{{ moreError() }}</span> }
        </div>
      }
    }`,
})
export class DataChangesPage {
  private readonly api = inject(AuditApiService);
  protected readonly ctx = inject(AuditContextService);

  readonly table = input<string>();
  readonly key = input<string>();
  readonly action = input<string>();
  readonly user = input<string>();
  readonly schema = input<string>();

  protected readonly actions = ['Inserted', 'Modified', 'Deleted'];

  private readonly filters = computed(() => ({
    table: this.table() ?? '', key: this.key() ?? '', action: this.action() ?? '', user: this.user() ?? '', schema: this.schema() ?? '',
  }));
  protected readonly form = linkedSignal(() => ({ ...this.filters() }));
  protected readonly filtered = computed(() => Object.values(this.filters()).some(v => v !== ''));

  protected readonly changes = cursorList(
    () => ({ filters: this.filters(), range: this.ctx.range(), abs: this.ctx.absolute() }),
    p => ({ ...this.ctx.window(), ...p.filters, limit: 50 }),
    q => this.api.dataChanges(q));
  protected readonly error = computed(() => problemText(this.changes.first.error()));
  protected readonly moreError = computed(() => problemText(this.changes.moreError()));

  apply() {
    const f = this.form();
    this.ctx.filter({
      table: f.table.trim() || null, key: f.key.trim() || null, action: f.action || null, user: f.user.trim() || null,
      schema: f.schema.trim() || null,
    });
  }

  clear() { this.ctx.filter({ table: null, key: null, action: null, user: null, schema: null }); }
}
