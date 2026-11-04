import { DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { apiResource } from './resource';
import { Observable, map } from 'rxjs';
import { AuditQuery } from '../core/api.service';
import { AuditPage } from '../core/models';

/**
 * A newest-first list read page by page with the API's cursor. The first page loads whenever `params` change; `more()`
 * appends the next page, asked with exactly the query of the first one (its time window included), so the list grows
 * without shifting, skipping or repeating rows. Call it in an injection context, such as a field initializer.
 */
export function cursorList<P, T>(params: () => P, query: (params: P) => AuditQuery, fetch: (query: AuditQuery) => Observable<AuditPage<T>>) {
  const destroyRef = inject(DestroyRef);
  const first = apiResource({
    params,
    stream: ({ params }) => {
      const q = query(params);
      return fetch(q).pipe(map(page => ({ page, query: q })));
    },
  });
  // Pages after the first; reset whenever the first page reloads.
  const rest = linkedSignal({ source: first.value, computation: () => ({ items: [] as T[], cursor: undefined as string | null | undefined }) });
  const loadingMore = signal(false);
  const moreError = signal<unknown>(null);
  const items = computed(() => [...(first.value()?.page.items ?? []), ...rest().items]);
  const cursor = computed(() => rest().cursor !== undefined ? rest().cursor! : first.value()?.page.nextCursor ?? null);

  function more() {
    const current = first.value();
    const next = cursor();
    if (!current || !next || loadingMore()) return;
    loadingMore.set(true);
    moreError.set(null);
    fetch({ ...current.query, cursor: next }).pipe(takeUntilDestroyed(destroyRef)).subscribe({
      next: page => rest.update(r => ({ items: [...r.items, ...page.items], cursor: page.nextCursor ?? null })),
      error: e => { moreError.set(e); loadingMore.set(false); },
      complete: () => loadingMore.set(false),
    });
  }

  return { first, items, hasMore: computed(() => cursor() !== null), loadingMore, moreError: moreError.asReadonly(), more };
}
