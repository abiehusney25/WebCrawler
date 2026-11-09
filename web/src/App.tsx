// =============================================================================
// App
// init: load the UI config and the health badge once, then route:
//   /            Start Crawl
//   /jobs/:id    Job Details
//   /history     History
// =============================================================================
import { useEffect, useState } from "react";
import { NavLink, Route, Routes } from "react-router-dom";
import { api, type AppConfig, type Health } from "./api";
import { ConfigContext, defaultConfig } from "./hooks/useConfig";
import { History } from "./pages/History";
import { JobDetails } from "./pages/JobDetails";
import { StartCrawl } from "./pages/StartCrawl";

export function App() {
  const [config, setConfig] = useState<AppConfig>(defaultConfig);
  const [health, setHealth] = useState<Health | null>(null);
  const [healthError, setHealthError] = useState(false);

  // init - config + health; both fail soft (defaults / red badge)
  useEffect(() => {
    api.getConfig().then(setConfig).catch(() => setConfig(defaultConfig));
    api.getHealth().then(setHealth).catch(() => setHealthError(true));
  }, []);

  return (
    <ConfigContext.Provider value={config}>
      <header>
        <h1>{config.appName}</h1>
        <nav>
          <NavLink to="/" end>Start Crawl</NavLink>
          <NavLink to="/history">History</NavLink>
        </nav>
        <span className={`badge ${healthError ? "fail" : health ? "ok" : ""}`} title="API health">
          {healthError ? "API down" : health ? `API ok (${health.dbMode}, ${health.queueMode})` : "checking..."}
        </span>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<StartCrawl />} />
          <Route path="/jobs/:jobId" element={<JobDetails />} />
          <Route path="/history" element={<History />} />
          <Route path="*" element={<section><h2>Page not found</h2><NavLink to="/">Start a crawl</NavLink></section>} />
        </Routes>
      </main>
    </ConfigContext.Provider>
  );
}
