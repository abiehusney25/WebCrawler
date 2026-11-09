// =============================================================================
// StatusBadge
// A job status as a colored badge (colors in style.css, one per status).
// =============================================================================
import type { JobStatus } from "../api";

export function StatusBadge({ status }: { status: JobStatus }) {
  return <span className={`badge status-${status}`}>{status}</span>;
}
