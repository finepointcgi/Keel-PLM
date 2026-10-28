export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly details?: unknown,
  ) {
    super(message);
  }
}

export type TokenSource = () => string;

export function createApi(token: TokenSource) {
  async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await fetch(path, {
      method,
      headers: {
        Authorization: `Bearer ${token()}`,
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    const data: unknown = text ? JSON.parse(text) : undefined;
    if (!res.ok) {
      const problem = data as { title?: string; errors?: Record<string, string[]> } | undefined;
      const messages = problem?.errors ? Object.values(problem.errors).flat() : [];
      throw new ApiError(res.status, messages.join(" ") || problem?.title || `Request failed (${res.status})`, data);
    }
    return data as T;
  }
  return {
    get: <T>(path: string) => request<T>("GET", path),
    post: <T>(path: string, body?: unknown) => request<T>("POST", path, body),
  };
}

export type Api = ReturnType<typeof createApi>;

export interface Me {
  id: string;
  displayName: string;
  usPerson: boolean;
  citizenship: string | null;
  org: string | null;
  programs: string[];
  exportAuthorizations: string[];
  cuiCategories: string[];
  roles: string[];
}

export interface AuditEvent {
  seq: number;
  id: string;
  occurredAt: string;
  actor: string;
  action: string;
  objectType: string | null;
  objectId: string | null;
  outcome: "Success" | "Denied" | "Failure";
  correlationId: string | null;
  actorName: string | null;
  payload: string;
  hash: string;
}

export interface VerificationResult {
  valid: boolean;
  eventsChecked: number;
  checkpointsChecked: number;
  lastSeq: number | null;
  lastHash: string | null;
  failures: string[];
}

export interface EvaluateResponse {
  permitted: boolean;
  reasons: string[];
  banner: string;
}
