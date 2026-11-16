let csrf = '';
export class ApiError extends Error { constructor(public status: number, public code: string, message: string) { super(message); } }
export async function refreshCsrf() { const r = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin' }); if (!r.ok) throw new Error('无法连接服务'); csrf = (await r.json()).token; }
export async function api<T = any>(path: string, method = 'GET', body?: unknown, revision?: number): Promise<T> {
  if (method !== 'GET' && !csrf) await refreshCsrf();
  const headers: Record<string,string> = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (method !== 'GET') { headers['X-CSRF-TOKEN'] = csrf; headers['Idempotency-Key'] = crypto.randomUUID(); }
  if (revision !== undefined) headers['If-Match'] = `"revision-${revision}"`;
  const response = await fetch('/api/v1' + path, { method, headers, credentials: 'same-origin', body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) { const problem = await response.json().catch(() => ({})); throw new ApiError(response.status, problem.code || problem.title || '', problem.detail || `请求失败 (${response.status})`); }
  return response.status === 204 ? undefined as T : response.json();
}
export interface Certificate {
  id: string; revision: number; name: string; domains: string[]; email: string; environment: string; validation: string;
  credentialId: string | null; webRoot: string | null; outputDirectory: string; fileName: string; format: string; autoRenew: boolean; termsAccepted: boolean;
  certificate: { status: string; notBeforeUtc: string | null; notAfterUtc: string | null; thumbprint: string | null; domains: string[]; currentGeneration: string | null; issuedRevision: number | null };
  renewal: { status: string; nextAttemptAtUtc: string | null; error: string | null };
}
