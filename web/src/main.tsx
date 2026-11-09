// =============================================================================
// main
// Entry point: React root + router (browser history; the API serves index.html
// for unknown paths so /jobs/{id} links work on reload).
// =============================================================================
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { App } from "./App";
import "./style.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </StrictMode>
);
