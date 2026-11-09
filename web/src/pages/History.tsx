// =============================================================================
// History
// Previous crawl jobs, newest first, paged. Click a row to open Job Details.
// =============================================================================
import { useEffect, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { api, ApiError, formatDate, showUrl, type JobHistory } from "../api";
import { StatusBadge } from "../components/StatusBadge";
import { useConfig } from "../hooks/useConfig";

export function History() {
  const config = useConfig();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const page = Math.max(1, Number(searchParams.get("page")) || 1);
  const [history, setHistory] = useState<JobHistory | null>(null);
  const [error, setError] = useState("");
  const [isLoading, setIsLoading] = useState(true);

  // loadHistory - one page of jobs
  useEffect(() => {
    let isActive = true;
    setIsLoading(true);
    setError("");
    api.getJobs(page, config.historyPageSize)
      .then(result => { if (isActive) setHistory(result); })
      .catch(err => { if (isActive) setError("FAIL loading history: " + (err as ApiError).message); })
      .finally(() => { if (isActive) setIsLoading(false); });
    return () => { isActive = false; };
  }, [page, config.historyPageSize]);

  const pageCount = history ? Math.max(1, Math.ceil(history.totalCount / history.pageSize)) : 1;

  return (
    <section>
      <h2>History</h2>
      {isLoading && <p className="muted">Loading jobs...</p>}
      {error && <p className="fail" role="alert">{error}</p>}
      {!isLoading && !error && history && history.items.length === 0 && <p className="muted">No crawl jobs yet.</p>}
      {history && history.items.length > 0 && (
        <table>
          <thead>
            <tr><th>Created</th><th>URL</th><th>Depth</th><th>Status</th><th>Completed</th></tr>
          </thead>
          <tbody>
            {history.items.map(job => (
              <tr key={job.jobId} className="clickable" onClick={() => navigate(`/jobs/${job.jobId}`)}>
                <td>{formatDate(job.createdAt)}</td>
                <td>{showUrl(job.url)}</td>
                <td>{job.maxDepth}</td>
                <td><StatusBadge status={job.status} /></td>
                <td>{formatDate(job.completedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {history && history.totalCount > history.pageSize && (
        <div className="pager">
          <button type="button" disabled={page <= 1} onClick={() => setSearchParams({ page: String(page - 1) })}>Previous</button>
          <span>Page {page} of {pageCount} ({history.totalCount} jobs)</span>
          <button type="button" disabled={page >= pageCount} onClick={() => setSearchParams({ page: String(page + 1) })}>Next</button>
        </div>
      )}
    </section>
  );
}
