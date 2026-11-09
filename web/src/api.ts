// =============================================================================
// api
// The API's shapes (same names as JobDtos.cs / PageNode.cs) and one getJson
// helper that turns ProblemDetails errors into a readable message.
// =============================================================================

export type JobStatus = "Pending" | "Running" | "Completed" | "Failed" | "Canceled";

export interface PageCounts {
  total: number;
  queued: number;
  running: number;
  done: number;
  skipped: number;
  failed: number;
}

export interface Job {
  jobId: string;
  url: string;
  maxDepth: number;
  status: JobStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  failureReason: string;
  summary: string;
  pageCounts: PageCounts | null;
  progress: number;
}

export interface JobHistory {
  items: Job[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface PageNode {
  id: number;
  url: string;
  finalUrl: string;
  depth: number;
  status: string;
  httpCode: number;
  title: string;
  internalLinks: number;
  externalLinks: number;
  totalLinks: number;
  linkRatio: number;
  error: string;
  children: PageNode[];
}

export interface Link {
  url: string;
  isInternal: boolean;
  timesFound: number;
}

export interface AppConfig {
  appName: string;
  pollIntervalMs: number;
  historyPageSize: number;
  defaultMaxDepth: number;
  maxDepthAllowed: number;
  liveUpdates: boolean;
}

export interface Health {
  status: string;
  dbMode: string;
  queueMode: string;
  workerMode: string;
}

export const finishedStatuses: JobStatus[] = ["Completed", "Failed", "Canceled"];

export class ApiError extends Error {
  status: number;
  fieldErrors: Record<string, string[]>;

  constructor(message: string, status: number, fieldErrors: Record<string, string[]> = {}) {
    super(message);
    this.status = status;
    this.fieldErrors = fieldErrors;
  }
}

// getJson - fetch /api/...; non-2xx -> ApiError with the ProblemDetails title/detail/errors
export async function getJson<T>(path: string, options?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch("/api" + path, {
      ...options,
      headers: { "Content-Type": "application/json", ...options?.headers }
    });
  } catch {
    throw new ApiError("The API can't be reached. Is it running?", 0);
  }

  const body = await response.json().catch(() => null);
  if (!response.ok) {
    const fieldErrors: Record<string, string[]> = body?.errors ?? {};
    const message = Object.values(fieldErrors).flat().join(" ") || body?.detail || body?.title || `HTTP ${response.status}`;
    throw new ApiError(message, response.status, fieldErrors);
  }
  return body as T;
}

export const api = {
  getConfig: () => getJson<AppConfig>("/config"),
  getHealth: () => getJson<Health>("/health"),
  createJob: (url: string, maxDepth?: number) =>
    getJson<{ jobId: string }>("/jobs", { method: "POST", body: JSON.stringify({ url, maxDepth }) }),
  getJob: (jobId: string) => getJson<Job>(`/jobs/${jobId}`),
  getJobs: (page: number, pageSize: number) => getJson<JobHistory>(`/jobs?page=${page}&pageSize=${pageSize}`),
  getTree: (jobId: string) => getJson<PageNode[]>(`/jobs/${jobId}/tree`),
  getLinks: (jobId: string, pageId: number) => getJson<Link[]>(`/jobs/${jobId}/pages/${pageId}/links`),
  cancelJob: (jobId: string) => getJson<{ jobId: string }>(`/jobs/${jobId}/cancel`, { method: "POST" })
};

// formatDate - local time, or "-" when not set yet
export function formatDate(value: string | null): string {
  return value ? new Date(value).toLocaleString() : "-";
}

// showUrl - readable url (%C3%A9 -> é); falls back to the raw text
export function showUrl(url: string): string {
  try {
    return decodeURI(url);
  } catch {
    return url;
  }
}
