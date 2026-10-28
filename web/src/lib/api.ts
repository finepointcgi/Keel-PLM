import { describeReason } from "./markings";

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly details?: unknown,
  ) {
    super(message);
  }
}

export type TokenSource = () => Promise<string>;

export function createApi(token: TokenSource) {
  async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await fetch(path, {
      method,
      headers: {
        Authorization: `Bearer ${await token()}`,
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    const data: unknown = text ? JSON.parse(text) : undefined;
    if (!res.ok) {
      const problem = data as { title?: string; errors?: Record<string, string[]>; reasons?: string[] } | undefined;
      const messages = problem?.errors
        ? Object.values(problem.errors).flat()
        : problem?.reasons
          ? problem.reasons.map(describeReason)
          : [];
      throw new ApiError(res.status, messages.join(" ") || problem?.title || `Request failed (${res.status})`, data);
    }
    return data as T;
  }
  return {
    get: <T>(path: string) => request<T>("GET", path),
    post: <T>(path: string, body?: unknown) => request<T>("POST", path, body),
    put: <T>(path: string, body?: unknown) => request<T>("PUT", path, body),
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

export interface Markings {
  exportJurisdiction: "None" | "Ear" | "Itar";
  eccn: string | null;
  usmlCategory: string | null;
  cuiCategories: string[];
  distributionStatement: "A" | "B" | "C" | "D" | "E" | "F";
  controllingOffice: string | null;
  distributionReason: string | null;
  program: string | null;
  owningOrg: string;
  exportAuthorizations: string[];
}

export type Attributes = Record<string, string | number | boolean>;

export interface PartSummary {
  id: string;
  number: string;
  name: string;
  partType: string;
  revision: string;
  iteration: number;
  state: string;
  banner: string;
  markings: Markings;
}

export interface PartSearchResult {
  items: PartSummary[];
  total: number;
  pageBanner: string;
}

export interface RevisionView {
  id: string;
  revision: string;
  state: string;
  stateLabel: string;
  isLatest: boolean;
  isLocked: boolean;
  latestIteration: number;
  createdAt: string;
  createdBy: string;
  stateChangedAt: string;
  stateChangedBy: string;
}

export interface IterationView {
  id: string;
  revision: string;
  iteration: number;
  label: string;
  description: string;
  attributes: Attributes;
  comment: string | null;
  createdAt: string;
  createdBy: string;
}

export interface PartDetail {
  id: string;
  number: string;
  name: string;
  partType: string;
  defaultUnit: string;
  source: string;
  lifecycleId: string;
  markings: Markings;
  banner: string;
  revision: RevisionView;
  iteration: IterationView;
  revisions: RevisionView[];
  history: IterationView[];
  checkout: {
    userId: string;
    userName: string | null;
    checkedOutAt: string;
    isMine: boolean;
    description: string | null;
    attributes: Attributes | null;
  } | null;
  transitions: { to: string; label: string; via: string; allowed: boolean; reason: string | null }[];
  permissions: { canModify: boolean; canAdminister: boolean; modifyDeniedReasons: string[] };
  createdAt: string;
  createdBy: string;
}

export interface AttributeDefinition {
  name: string;
  label: string;
  dataType: "string" | "number" | "boolean" | "enum";
  unit: string | null;
  enumValues: string[] | null;
  required: boolean;
  maxLength: number | null;
}

export interface PartMetadata {
  partTypes: string[];
  sources: string[];
  units: string[];
  attributes: AttributeDefinition[];
  lifecycle: { id: string; name: string; states: { state: string; label: string; isLocked: boolean }[] };
}
