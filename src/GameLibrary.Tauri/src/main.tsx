import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./index.css";
import { SettingsProvider } from "./lib/settings";
import { LibraryProvider } from "./lib/state";
import { ErrorBoundary } from "./components/ErrorBoundary";

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <ErrorBoundary>
      <SettingsProvider>
        <LibraryProvider>
          <App />
        </LibraryProvider>
      </SettingsProvider>
    </ErrorBoundary>
  </React.StrictMode>,
);
