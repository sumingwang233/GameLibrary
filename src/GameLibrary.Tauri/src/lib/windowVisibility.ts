import { isTauri } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";

/** Native hide/minimize state, not focus: an unfocused window may still be visible. */
export function watchWindowVisibility(changed: (visible: boolean) => void) {
  let active = true;
  let revision = 0;
  let lastVisible = true;
  const native = isTauri();
  let unlisten: (() => void) | undefined;
  const publish = (visible: boolean) => {
    revision++;
    if (active && visible !== lastVisible) {
      lastVisible = visible;
      changed(visible);
    }
  };
  const fromDocument = () => { if (!native) publish(!document.hidden); };
  document.addEventListener("visibilitychange", fromDocument);
  if (native) {
    void listen<boolean>("gamelibrary-window-visibility", event => publish(event.payload)).then(async stop => {
      if (!active) { stop(); return; }
      unlisten = stop;
      const snapshot = revision;
      const current = getCurrentWindow();
      const [visible, minimized] = await Promise.all([current.isVisible(), current.isMinimized()]);
      if (active && snapshot === revision) publish(visible && !minimized);
    }).catch(() => { /* Keep ordinary refresh working if native visibility is unavailable. */ });
  } else fromDocument();
  return () => {
    active = false;
    document.removeEventListener("visibilitychange", fromDocument);
    unlisten?.();
  };
}
