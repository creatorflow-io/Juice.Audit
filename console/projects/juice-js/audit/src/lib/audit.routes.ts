import { Routes } from '@angular/router';
import { Menus } from '@juice-js/core';
import { normalizeBasePath } from './core/options';

function page(path: string, title: string, load: Routes[number]['loadComponent']): Routes[number] {
  return { path, title: `${title} · Juice Audit`, loadComponent: load };
}

/**
 * The audit pages. Detail pages are deep links and carry the time window in their query parameters. The host mounts
 * them at its `basePath` (see {@link provideJuiceAudit}) and adds its own guards.
 */
export const AUDIT_ROUTES: Routes = [
  page('', 'Overview', () => import('./pages/overview.page').then(m => m.OverviewPage)),
  page('access-logs', 'Access records', () => import('./pages/access-logs.page').then(m => m.AccessLogsPage)),
  page('access-logs/:id', 'Access record', () => import('./pages/access-log.page').then(m => m.AccessLogPage)),
  page('data-changes', 'Data changes', () => import('./pages/data-changes.page').then(m => m.DataChangesPage)),
  page('data-changes/:id', 'Data change', () => import('./pages/data-change.page').then(m => m.DataChangePage)),
  page('traces/:traceId', 'Trace', () => import('./pages/trace.page').then(m => m.TracePage)),
];

/**
 * The side menu entries (juice-layout), linking under `basePath`. Their ids start with `audit-`, so they sit beside
 * another package's menus (such as @juice-js/observability's) in one MenuModule.
 */
export function auditMenus(basePath = '/', order = 1): Menus {
  const base = normalizeBasePath(basePath);
  const link = (path: string) => (base === '/' ? `/${path}` : path ? `${base}/${path}` : base);
  return [
    { id: 'audit-overview', label: 'Audit', icon: 'policy', routerLink: link('') },
    { id: 'audit-access-logs', label: 'Access records', icon: 'manage_search', routerLink: link('access-logs') },
    { id: 'audit-data-changes', label: 'Data changes', icon: 'difference', routerLink: link('data-changes') },
  ].map((menu, index) => ({ ...menu, order: order + index }));
}
