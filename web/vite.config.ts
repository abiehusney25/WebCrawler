// =============================================================================
// vite.config
// "npm run build" writes the app into the API's wwwroot, so ONE origin serves the
// UI and the API (no CORS, no second web server). "npm run dev" (port 5173)
// proxies /api to the API on port 5000 for hot reload while developing.
// =============================================================================
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../src/WebCrawler.Api/wwwroot",
    emptyOutDir: true
  },
  server: {
    port: 5173,
    proxy: { "/api": "http://localhost:5000" }
  }
});
