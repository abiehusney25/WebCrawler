// =============================================================================
// JobDetails
// Status, timestamps, progress (live while running), cancel, then the page
// tree with each page's Domain Link Ratio. Loading, not-found and error states.
// =============================================================================
import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api, ApiError, finishedStatuses, formatDate, showUrl } from "../api";
import { PageTree } from "../components/PageTree";
import { StatusBadge } from "../components/StatusBadge";
import { useJob } from "../hooks/useJob";

export function JobDetails() {
  const { jobId = "" } = useParams();
  const { job, tree, error, isLive, isNotFound, refresh } = useJob(jobId);
  const [cancelError, setCancelError] = useState("");

  if (isNotFound)
    return <section><h2>Job not found</h2><p className="fail">{error}</p><Link to="/">Start a new crawl</Link></section>;
  if (!job)
    return <section><h2>Job Details</h2><p className="muted">{error ? <span className="fail">{error}</span> : "Loading job..."}</p></section>;

  const isFinished = finishedStatuses.includes(job.status);
  const counts = job.pageCounts;

  // cancelJob - 409 means it finished in the meantime; refresh either way
  async function cancelJob() {
    setCancelError("");
    try {
      await api.cancelJob(jobId);
    } catch (err) {
      setCancelError("FAIL " + (err as ApiError).message);
    }
    refresh();
  }

  return (
    <>
      <section>
        <h2>Job Details <StatusBadge status={job.status} /></h2>
        <dl>
          <dt>Job id</dt><dd>{job.jobId}</dd>
          <dt>URL</dt><dd>{showUrl(job.url)}</dd>
          <dt>Max depth</dt><dd>{job.maxDepth}</dd>
          <dt>Created</dt><dd>{formatDate(job.createdAt)}</dd>
          <dt>Started</dt><dd>{formatDate(job.startedAt)}</dd>
          <dt>Completed</dt><dd>{formatDate(job.completedAt)}</dd>
          {counts && <><dt>Pages</dt><dd>{counts.total} found: {counts.done} done, {counts.skipped} skipped, {counts.failed} failed, {counts.queued + counts.running} waiting</dd></>}
          {job.failureReason && <><dt>Reason</dt><dd className="fail">{job.failureReason}</dd></>}
        </dl>
        <progress value={job.progress} max={1} aria-label="progress" />
        <p className="muted">
          {Math.round(job.progress * 100)}%
          {!isFinished && (isLive ? " - live updates" : " - refreshing")}
        </p>
        {!isFinished && <button type="button" onClick={cancelJob}>Cancel job</button>}
        {cancelError && <p className="fail">{cancelError}</p>}
        {error && <p className="fail">{error}</p>}
      </section>

      <section>
        <h2>Pages {!isFinished && <span className="muted">(growing while the crawl runs)</span>}</h2>
        <p className="muted">DLR (Domain Link Ratio) = links staying on the starting domain / all outgoing links; 0 when a page has no links.</p>
        {tree.length === 0
          ? <p className="muted">{isFinished ? "No pages were crawled." : "Waiting for the first page..."}</p>
          : <PageTree jobId={jobId} nodes={tree} />}
      </section>
    </>
  );
}
