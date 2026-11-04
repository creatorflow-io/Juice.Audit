import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AUDIT_OPTIONS } from './options';
import {
  AccessLogDetail, AccessLogSummary, AuditOverview, AuditPage, AuditTrace, DataChangeDetail, DataChangeSummary,
} from './models';

export type AuditQuery = Record<string, string | number | boolean | null | undefined>;

/**
 * The audit API that Juice.Audit.Api maps with MapJuiceAudit(). It is scoped to the caller's tenant on the server, and
 * the host's bearer-token interceptor authenticates the calls.
 */
@Injectable({ providedIn: 'root' })
export class AuditApiService {
  private readonly http = inject(HttpClient);
  private readonly base = (() => {
    const o = inject(AUDIT_OPTIONS);
    return `${o.apiBase.replace(/\/+$/, '')}/${o.apiPath.replace(/^\/+|\/+$/g, '')}`;
  })();

  private get<T>(path: string, query: AuditQuery = {}): Observable<T> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value === null || value === undefined || value === '') continue;
      params = params.set(key, String(value));
    }
    return this.http.get<T>(`${this.base}${path}`, { params });
  }

  /** Access records, newest first; pass the previous page's `nextCursor` as `cursor` for the next. */
  accessLogs(query: AuditQuery) { return this.get<AuditPage<AccessLogSummary>>('/access-logs', query); }
  accessLog(id: string) { return this.get<AccessLogDetail>(`/access-logs/${id}`); }
  /** Data changes, newest first, paged like {@link accessLogs}. */
  dataChanges(query: AuditQuery) { return this.get<AuditPage<DataChangeSummary>>('/data-changes', query); }
  dataChange(id: string) { return this.get<DataChangeDetail>(`/data-changes/${id}`); }
  trace(traceId: string) { return this.get<AuditTrace>(`/traces/${encodeURIComponent(traceId)}`); }
  overview(query: AuditQuery) { return this.get<AuditOverview>('/overview', query); }
}
