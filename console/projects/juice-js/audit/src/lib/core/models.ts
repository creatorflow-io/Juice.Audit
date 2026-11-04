/** The audit API's shapes (Juice.Audit.Api, MapJuiceAudit()). Times are ISO 8601. */

export interface AuditPage<T> { items: T[]; nextCursor?: string | null; }

/** One audited HTTP request, message, mediator request or notification. */
export interface AccessLogSummary {
  id: string;
  dateTime: string;
  user: string | null;
  tenantId: string | null;
  action: string;
  /** The HTTP method, or MESSAGE, REQUEST or NOTIFICATION for work outside a request. */
  method: string | null;
  path: string | null;
  status: number | null;
  elapsedMs: number | null;
  app: string | null;
  traceId: string | null;
  isRestricted: boolean;
}

export interface AccessLogDetail {
  id: string;
  dateTime: string;
  user: string | null;
  tenantId: string | null;
  action: string;
  traceId: string | null;
  isRestricted: boolean;
  metadata: Record<string, string | null>;
  request: {
    method: string | null; scheme: string | null; host: string | null; path: string | null; query: string | null;
    headers: string | null; data: string | null; remoteIp: string | null; zone: string | null;
  } | null;
  response: {
    status: number | null; elapsedMs: number | null; message: string | null; error: string | null;
    headers: string | null; data: string | null;
  } | null;
  server: { app: string | null; appVersion: string | null; machine: string | null; os: string | null } | null;
  /** Data changes recorded under the same trace id. */
  dataChanges: number;
}

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

/** A row change: inserted, modified or deleted, with its keys. */
export interface DataChangeSummary {
  id: string;
  dateTime: string;
  user: string | null;
  tenantId: string | null;
  action: string;
  database: string | null;
  schema: string | null;
  table: string;
  keys: JsonValue;
  traceId: string | null;
}

export interface DataChangeDetail extends DataChangeSummary {
  /** `{ OriginalValues: {…}, CurrentValues: {…} }`, as the audited app reported them. */
  changes: JsonValue;
}

export interface AuditTrace {
  traceId: string;
  accessLogs: AccessLogDetail[];
  dataChanges: DataChangeDetail[];
  /** More records share the trace than the API returns. */
  truncated: boolean;
}

export interface AuditCount { name: string; count: number; errors: number; }

export interface AuditOverview {
  from: string;
  to: string;
  requests: number;
  clientErrors: number;
  serverErrors: number;
  restricted: number;
  dataChanges: number;
  statuses: AuditCount[];
  apps: AuditCount[];
  users: AuditCount[];
  tables: AuditCount[];
  changeActions: AuditCount[];
  recentErrors: AccessLogSummary[];
  slowest: AccessLogSummary[];
}

/** An access record's list row, from its detail (the trace endpoint returns details). */
export function accessLogSummary(d: AccessLogDetail): AccessLogSummary {
  return {
    id: d.id, dateTime: d.dateTime, user: d.user, tenantId: d.tenantId, action: d.action,
    method: d.request?.method ?? null, path: d.request?.path ?? null, status: d.response?.status ?? null,
    elapsedMs: d.response?.elapsedMs ?? null, app: d.server?.app ?? null, traceId: d.traceId, isRestricted: d.isRestricted,
  };
}
