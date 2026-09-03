"use client";

/**
 * Error boundary around the R3F canvas (task 37.2). If the WebGL scene throws (e.g. a lost
 * context), we show a readable message and the underlying error instead of a blank/white
 * canvas, and keep the HUD controls usable. This is a presentation safety net only.
 */

import { Component, type ReactNode } from "react";

interface Props {
  children: ReactNode;
}

interface State {
  error: Error | null;
}

export class SceneErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error): void {
    console.error("[Forge scene] render error:", error);
  }

  render(): ReactNode {
    if (this.state.error) {
      return (
        <div className="flex h-full w-full items-center justify-center p-6 text-center">
          <div className="max-w-md rounded-lg bg-black/50 p-4 text-sm text-white/80 backdrop-blur">
            <p className="mb-2 font-semibold text-amber-300">
              The 3D view hit a rendering error.
            </p>
            <p className="mb-3 text-white/60">
              The engine and controls are still live; only the WebGL scene failed.
            </p>
            <pre className="overflow-auto whitespace-pre-wrap text-left text-[11px] text-white/50">
              {this.state.error.message}
            </pre>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}