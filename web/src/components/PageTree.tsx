// =============================================================================
// PageTree
// The job's pages as nested lists (each page under the first parent that found
// it): status, depth, link counts and Domain Link Ratio. A page's links load
// on demand (they can be up to 1,000 per page). Crawled text is rendered as
// text by React, never as html.
// =============================================================================
import { useState } from "react";
import { api, ApiError, showUrl, type Link, type PageNode } from "../api";

export function PageTree({ jobId, nodes }: { jobId: string; nodes: PageNode[] }) {
  return (
    <ul className="tree">
      {nodes.map(node => <TreeNode key={node.id} jobId={jobId} node={node} />)}
    </ul>
  );
}

function TreeNode({ jobId, node }: { jobId: string; node: PageNode }) {
  const isFinished = node.status === "Done" || node.status === "Skipped" || node.status === "Failed";
  return (
    <li>
      <span className={`page-${node.status}`}>[{node.status}]</span>{" "}
      {node.title && <span className="page-title">{node.title} </span>}
      <a href={node.url} target="_blank" rel="noopener noreferrer">{showUrl(node.url)}</a>
      <div className="muted">
        depth {node.depth}
        {node.status === "Done" && <> | links {node.totalLinks} (internal {node.internalLinks}, external {node.externalLinks}) | <b>DLR {node.linkRatio.toFixed(3)}</b></>}
        {node.httpCode > 0 && <> | HTTP {node.httpCode}</>}
        {node.error && <> | {node.error}</>}
      </div>
      {isFinished && node.totalLinks > 0 && <PageLinks jobId={jobId} pageId={node.id} />}
      {node.children.length > 0 && <PageTree jobId={jobId} nodes={node.children} />}
    </li>
  );
}

// PageLinks - "show links" toggles the page's outgoing links (loaded once)
function PageLinks({ jobId, pageId }: { jobId: string; pageId: number }) {
  const [links, setLinks] = useState<Link[] | null>(null);
  const [isOpen, setIsOpen] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState("");

  async function toggleLinks() {
    if (isOpen) {
      setIsOpen(false);
      return;
    }
    setIsOpen(true);
    if (links)
      return;
    setIsLoading(true);
    setError("");
    try {
      setLinks(await api.getLinks(jobId, pageId));
    } catch (err) {
      setError("FAIL loading links: " + (err as ApiError).message);
    } finally {
      setIsLoading(false);
    }
  }

  return (
    <div>
      <button type="button" className="link-button" onClick={toggleLinks}>{isOpen ? "hide links" : "show links"}</button>
      {isOpen && isLoading && <p className="muted">Loading links...</p>}
      {isOpen && error && <p className="fail">{error}</p>}
      {isOpen && links && (
        <ul className="links">
          {links.map(link => (
            <li key={link.url} className={link.isInternal ? "internal" : "external"}>
              {link.isInternal ? "internal" : "external"}{" "}
              <a href={link.url} target="_blank" rel="noopener noreferrer">{showUrl(link.url)}</a>
              {link.timesFound > 1 && <span className="muted"> x{link.timesFound}</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
