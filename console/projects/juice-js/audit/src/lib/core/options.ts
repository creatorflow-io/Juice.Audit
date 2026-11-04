import { EnvironmentProviders, Injectable, InjectionToken, Pipe, PipeTransform, inject, makeEnvironmentProviders } from '@angular/core';

/** How the host app mounts Juice Audit. */
export interface AuditOptions {
  /** The origin of the API that maps MapJuiceAudit(); empty when it serves the app (same origin). */
  apiBase: string;
  /** Where MapJuiceAudit() mapped the API on that origin (its `prefix`). */
  apiPath: string;
  /** Where the host mounts {@link AUDIT_ROUTES}: `/` for the whole app, or a path such as `/audit`. */
  basePath: string;
  /**
   * A link to a trace in a tracing UI, shown beside the trace id of each record: for the Juice Observability pages
   * mounted at `/observability`, `id => ['/observability/traces', id]`. A string is an external URL. Without it, no link.
   */
  traceLink?: ((traceId: string) => unknown[] | string | null) | null;
}

export const AUDIT_OPTIONS = new InjectionToken<AuditOptions>('AUDIT_OPTIONS', {
  providedIn: 'root',
  factory: () => ({ apiBase: '', apiPath: '/api/audit', basePath: '/', traceLink: null }),
});

/** Configures the pages: where the API is, where the host mounted the routes, and where a trace opens. */
export function provideJuiceAudit(options: Partial<AuditOptions> = {}): EnvironmentProviders {
  return makeEnvironmentProviders([
    {
      provide: AUDIT_OPTIONS,
      useValue: {
        apiBase: options.apiBase ?? '',
        apiPath: options.apiPath ?? '/api/audit',
        basePath: options.basePath ?? '/',
        traceLink: options.traceLink ?? null,
      } satisfies AuditOptions,
    },
  ]);
}

/** `/audit/` → `/audit`; `` and `/` → `/`. */
export function normalizeBasePath(basePath: string): string {
  const trimmed = basePath.trim().replace(/^\/+|\/+$/g, '');
  return trimmed ? `/${trimmed}` : '/';
}

/** Router links to the pages, under the host's mount path. */
@Injectable({ providedIn: 'root' })
export class AuditLinks {
  private readonly options = inject(AUDIT_OPTIONS);
  readonly base = normalizeBasePath(this.options.basePath);

  /** `to('access-logs', id)` → `['/audit', 'access-logs', id]`. */
  to(...segments: unknown[]): unknown[] { return [this.base, ...segments]; }

  /** The host's link to a trace in its tracing UI, if it gave one. */
  externalTrace(traceId: string | null | undefined): { route: unknown[] } | { href: string } | null {
    if (!traceId || !this.options.traceLink) return null;
    const link = this.options.traceLink(traceId);
    if (!link) return null;
    return typeof link === 'string' ? { href: link } : { route: link };
  }
}

/** `['access-logs', id] | jaLink`: the same as {@link AuditLinks.to} in a template. */
@Pipe({ name: 'jaLink' })
export class AuditLinkPipe implements PipeTransform {
  private readonly links = inject(AuditLinks);
  transform(segments: unknown[]): unknown[] { return this.links.to(...segments); }
}
