// =============================================================================
// useJob
// One job, kept up to date while it runs: live server-sent events from
// /api/jobs/{id}/events (stretch goal), falling back to polling every
// PollIntervalMs when SSE is off or the stream breaks. The page tree reloads
// with every change, so it grows on screen while the crawl runs.
// =============================================================================
import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, finishedStatuses, type Job, type PageNode } from "../api";
import { useConfig } from "./useConfig";

export interface JobState {
  job: Job | null;
  tree: PageNode[];
  error: string;
  isLive: boolean;
  isNotFound: boolean;
  refresh: () => void;
}

export function useJob(jobId: string): JobState {
  const config = useConfig();
  const [job, setJob] = useState<Job | null>(null);
  const [tree, setTree] = useState<PageNode[]>([]);
  const [error, setError] = useState("");
  const [isLive, setIsLive] = useState(false);
  const [isNotFound, setIsNotFound] = useState(false);
  const [refreshCount, setRefreshCount] = useState(0);
  const isActive = useRef(true);

  // showJob - one update (from SSE or a poll): job fields + the tree
  const showJob = useCallback(async (next: Job) => {
    setJob(next);
    const pages = await api.getTree(jobId);
    if (isActive.current)
      setTree(pages);
  }, [jobId]);

  useEffect(() => {
    isActive.current = true;
    let pollTimer: number | undefined;
    let events: EventSource | undefined;

    // pollJob - refresh, then again every PollIntervalMs until the job is finished
    async function pollJob() {
      try {
        const next = await api.getJob(jobId);
        if (!isActive.current)
          return;
        await showJob(next);
        setError("");
        if (!finishedStatuses.includes(next.status))
          pollTimer = window.setTimeout(pollJob, config.pollIntervalMs);
      } catch (err) {
        if (!isActive.current)
          return;
        const apiError = err as ApiError;
        setIsNotFound(apiError.status === 404);
        setError(apiError.message + (apiError.status === 404 ? "" : " - retrying"));
        if (apiError.status !== 404)
          pollTimer = window.setTimeout(pollJob, config.pollIntervalMs);   // a network blip should not freeze the screen
      }
    }

    // watchJob - SSE when allowed; any stream error -> plain polling
    function watchJob() {
      if (!config.liveUpdates || !window.EventSource) {
        pollJob();
        return;
      }
      events = new EventSource(`/api/jobs/${jobId}/events`);
      setIsLive(true);
      events.addEventListener("job", event => {
        const next = JSON.parse((event as MessageEvent).data) as Job;
        if (finishedStatuses.includes(next.status))
          events?.close();                                   // close before the server ends the stream
        showJob(next).then(() => setError("")).catch(err => setError((err as Error).message));
      });
      events.onerror = () => {
        events?.close();
        setIsLive(false);
        pollJob();                                           // stops by itself once the job is finished
      };
    }

    watchJob();
    return () => {
      isActive.current = false;
      events?.close();
      window.clearTimeout(pollTimer);
    };
  }, [jobId, config.liveUpdates, config.pollIntervalMs, showJob, refreshCount]);

  const refresh = useCallback(() => setRefreshCount(count => count + 1), []);
  return { job, tree, error, isLive, isNotFound, refresh };
}
