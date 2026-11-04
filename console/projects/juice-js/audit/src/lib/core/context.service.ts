import { Injectable, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Params, Router } from '@angular/router';
import { filter, map, startWith } from 'rxjs';

export const AUDIT_RANGES: Record<string, number> = {
  '1h': 3_600_000, '6h': 6 * 3_600_000, '24h': 24 * 3_600_000, '7d': 7 * 24 * 3_600_000, '30d': 30 * 24 * 3_600_000,
};

/**
 * The time window that follows the user between the audit pages. It lives in the URL (`range`, or `from` and `to`), so
 * every page is a deep link and the back button restores it.
 */
@Injectable({ providedIn: 'root' })
export class AuditContextService {
  private readonly router = inject(Router);

  // Created on first use, after the first navigation may already have ended: start from the current URL.
  private readonly params = toSignal(this.router.events.pipe(
    filter(e => e instanceof NavigationEnd),
    startWith(null),
    map(() => this.leaf().snapshot.queryParams),
  ), { initialValue: {} as Params });

  readonly range = computed(() => {
    const p = this.params();
    return p['from'] && p['to'] ? 'custom' : ((p['range'] as string | undefined) ?? '24h');
  });
  readonly absolute = computed(() => {
    const p = this.params();
    return p['from'] && p['to'] ? { from: p['from'] as string, to: p['to'] as string } : null;
  });

  /** The window as ISO strings, evaluated now for a relative range. */
  window(): { from: string; to: string } {
    const absolute = this.absolute();
    if (absolute) return absolute;
    const to = Date.now();
    return { from: new Date(to - (AUDIT_RANGES[this.range()] ?? AUDIT_RANGES['24h'])).toISOString(), to: new Date(to).toISOString() };
  }

  /** Query parameters for a link: the window plus the target page's own. */
  keep(extra: Params = {}): Params {
    const p = this.params();
    const context: Params = {};
    for (const key of ['range', 'from', 'to']) if (p[key]) context[key] = p[key];
    return { ...context, ...extra };
  }

  /** Changes the window on the current page, keeping its other parameters. */
  set(change: { range?: string | null; from?: string | null; to?: string | null }) {
    const queryParams: Params = { ...change };
    if (change.range) Object.assign(queryParams, { from: null, to: null });
    void this.router.navigate([], { relativeTo: this.leaf(), queryParams, queryParamsHandling: 'merge' });
  }

  /** Sets the current page's own query parameters (its filters), keeping the window. */
  filter(change: Params) {
    void this.router.navigate([], { relativeTo: this.leaf(), queryParams: change, queryParamsHandling: 'merge', replaceUrl: true });
  }

  private leaf(): ActivatedRoute {
    let route = this.router.routerState.root;
    while (route.firstChild) route = route.firstChild;
    return route;
  }
}
