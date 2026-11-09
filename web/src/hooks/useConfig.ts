// =============================================================================
// useConfig
// UI settings from /api/config (Config/settings.csv), loaded once per app.
// The defaults keep the screens working if the call fails.
// =============================================================================
import { createContext, useContext } from "react";
import type { AppConfig } from "../api";

export const defaultConfig: AppConfig = {
  appName: "Web Crawler",
  pollIntervalMs: 1500,
  historyPageSize: 20,
  defaultMaxDepth: 2,
  maxDepthAllowed: 5,
  liveUpdates: true
};

export const ConfigContext = createContext<AppConfig>(defaultConfig);

export function useConfig(): AppConfig {
  return useContext(ConfigContext);
}
