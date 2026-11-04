import { Pipe, PipeTransform } from '@angular/core';
import { JsonValue } from '../core/models';

/** 0.8 ms, 120 ms, 1.42 s, 2 m 05 s. */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || Number.isNaN(ms)) return '—';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(2)} s`;
  const s = Math.round(ms / 1000);
  return `${Math.floor(s / 60)} m ${String(s % 60).padStart(2, '0')} s`;
}

/** A JSON value as one line: strings bare, everything else as JSON. */
export function formatValue(value: JsonValue | undefined): string {
  if (value === undefined) return '';
  if (value === null) return 'null';
  return typeof value === 'string' ? value : JSON.stringify(value);
}

/** The keys of a changed row, `Id=42, Tenant=a`, from the JSON object the API returns. */
export function formatKeys(keys: JsonValue): string {
  if (keys && typeof keys === 'object' && !Array.isArray(keys)) {
    return Object.entries(keys).map(([k, v]) => `${k}=${formatValue(v)}`).join(', ');
  }
  return formatValue(keys);
}

/** Text that may be JSON, indented when it is. */
export function prettyJson(text: string | null | undefined): string {
  if (!text) return '';
  try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; }
}

/** A status's badge: 2xx and 3xx ok, 4xx a warning, 5xx an error. */
export function statusClass(status: number | null | undefined): string {
  if (!status) return 'ja-tone-none';
  return status >= 500 ? 'ja-tone-bad' : status >= 400 ? 'ja-tone-warn' : 'ja-tone-ok';
}

/** Inserted, Modified, Deleted (and anything else an app reports). */
export function actionClass(action: string): string {
  const a = action.toLowerCase();
  return a.startsWith('insert') || a === 'added' ? 'ja-tone-ok'
    : a.startsWith('delete') ? 'ja-tone-bad'
    : a.startsWith('modif') || a.startsWith('update') ? 'ja-tone-info' : 'ja-tone-none';
}

@Pipe({ name: 'jaDuration' })
export class DurationPipe implements PipeTransform {
  transform(ms: number | null | undefined) { return formatDuration(ms); }
}

@Pipe({ name: 'jaKeys' })
export class KeysPipe implements PipeTransform {
  transform(keys: JsonValue) { return formatKeys(keys); }
}

@Pipe({ name: 'jaPretty' })
export class PrettyJsonPipe implements PipeTransform {
  transform(text: string | null | undefined) { return prettyJson(text); }
}

/** Shows a request's failure as a short line instead of a broken page. */
export function problemText(error: unknown): string {
  const e = error as { status?: number; error?: { title?: string; detail?: string } } | undefined;
  if (!e) return '';
  if (e.status === 0) return 'The audit API cannot be reached.';
  if (e.status === 401 || e.status === 403) return 'You are not allowed to read the audit records.';
  return [e.error?.title, e.error?.detail].filter(Boolean).join(': ') || `Request failed (${e.status}).`;
}
