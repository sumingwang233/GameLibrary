import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./index.css";
import { LibraryProvider } from "./lib/state";

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <LibraryProvider><App /></LibraryProvider>
  </React.StrictMode>,
);
