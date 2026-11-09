// =============================================================================
// StartCrawl
// URL + optional max depth -> POST /api/jobs -> straight to Job Details.
// Quick checks run in the browser; the API's 400 errors are shown per field.
// =============================================================================
import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { api, ApiError } from "../api";
import { useConfig } from "../hooks/useConfig";

export function StartCrawl() {
  const config = useConfig();
  const navigate = useNavigate();
  const [url, setUrl] = useState("");
  const [maxDepth, setMaxDepth] = useState("");
  const [error, setError] = useState("");
  const [isSending, setIsSending] = useState(false);

  // startJob - check, send, navigate; any error stays on the form
  async function startJob(event: FormEvent) {
    event.preventDefault();
    const checkError = checkInput(url, maxDepth, config.maxDepthAllowed);
    if (checkError) {
      setError(checkError);
      return;
    }

    setIsSending(true);
    setError("");
    try {
      const created = await api.createJob(url.trim(), maxDepth === "" ? undefined : Number(maxDepth));
      navigate(`/jobs/${created.jobId}`);
    } catch (err) {
      setError("FAIL " + (err as ApiError).message);
      setIsSending(false);
    }
  }

  return (
    <section>
      <h2>Start Crawl</h2>
      <form onSubmit={startJob} noValidate>
        <label>
          Website URL
          <input className="url-input" type="url" placeholder="https://example.com" value={url}
            onChange={event => setUrl(event.target.value)} autoFocus />
        </label>
        <label>
          Max depth (0-{config.maxDepthAllowed})
          <input className="depth-input" type="number" min={0} max={config.maxDepthAllowed}
            placeholder={String(config.defaultMaxDepth)} value={maxDepth}
            onChange={event => setMaxDepth(event.target.value)} />
        </label>
        <button type="submit" disabled={isSending}>{isSending ? "Starting..." : "Start Crawl"}</button>
      </form>
      <p className="muted">Crawls pages on the same site up to the max depth (default {config.defaultMaxDepth}). External links are counted, never followed.</p>
      {error && <p className="fail" role="alert">{error}</p>}
    </section>
  );
}

// checkInput - the same rules as the API, so most mistakes never leave the browser
export function checkInput(url: string, maxDepth: string, maxDepthAllowed: number): string {
  let parsed: URL;
  try {
    parsed = new URL(url.trim());
  } catch {
    return "Enter an absolute URL, e.g. https://example.com";
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:")
    return "Only http and https URLs can be crawled.";
  if (maxDepth !== "") {
    const depth = Number(maxDepth);
    if (!Number.isInteger(depth) || depth < 0 || depth > maxDepthAllowed)
      return `Max depth must be a whole number from 0 to ${maxDepthAllowed}.`;
  }
  return "";
}
