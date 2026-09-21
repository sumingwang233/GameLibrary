import { Component, type ErrorInfo, type ReactNode } from "react";

export class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error("GameLibrary render failed", error, info.componentStack);
  }

  render() {
    if (this.state.failed) {
      return <main role="alert" className="flex min-h-screen flex-col items-center justify-center gap-4 bg-background p-6 text-text-primary">
        <h1 className="text-lg font-semibold">界面暂时无法显示</h1>
        <p className="text-sm text-text-secondary">请重新加载界面。已保存的游戏库数据不受影响。</p>
        <button className="rounded border border-border px-4 py-2" onClick={() => window.location.reload()}>重新加载</button>
      </main>;
    }
    return this.props.children;
  }
}
