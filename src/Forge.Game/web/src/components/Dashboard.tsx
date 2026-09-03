"use client";

/**
 * Top-level operations dashboard: the pseudo-3D view with HUD overlays (tasks 37.2/37.3).
 *
 * Reads the authoritative Forge state from context and composes the operations view with
 * the metrics HUD, operator controls, connection status, and event feed. When no snapshot
 * has arrived yet (engine offline / connecting) it shows a graceful placeholder instead
 * of crashing (Req offline handling).
 */

import dynamic from "next/dynamic";

import { ConnectionStatus } from "@/components/hud/ConnectionStatus";
import { EventLog } from "@/components/hud/EventLog";
import { MetricsHud } from "@/components/hud/MetricsHud";
import { OperatorPanel } from "@/components/hud/OperatorPanel";
import { SceneErrorBoundary } from "@/components/scene/SceneErrorBoundary";
import { useForge } from "@/lib/ForgeProvider";

// The R3F canvas is browser-only (WebGL); load it without SSR.
const OperationsView = dynamic(
  () =>
    import("@/components/scene/OperationsView").then((m) => m.OperationsView),
  { ssr: false },
);

export function Dashboard() {
  const state = useForge();
  const snapshot = state.snapshot;
  const connected = state.status === "connected";

  return (
    <main className="relative h-full w-full overflow-hidden bg-[#0b0f14] text-white">
      {/* 3D operations view fills the viewport. */}
      <div className="absolute inset-0">
        {snapshot ? (
          <SceneErrorBoundary>
            <OperationsView snapshot={snapshot} snapSeq={state.seq} />
          </SceneErrorBoundary>
        ) : (
          <div className="flex h-full w-full items-center justify-center">
            <div className="flex flex-col items-center gap-3 text-center">
              <div className="h-3 w-3 animate-ping rounded-full bg-sky-400" />
              <p className="text-sm text-white/60">
                {state.status === "disconnected"
                  ? "Engine offline. Waiting for the headless engine at :5195…"
                  : "Connecting to engine…"}
              </p>
            </div>
          </div>
        )}
      </div>

      {/* HUD overlays — pointer events limited to the panels themselves. */}
      <div className="pointer-events-none absolute inset-0 flex flex-col justify-between p-4">
        <div className="flex items-start justify-between gap-4">
          <div className="flex flex-col gap-3">
            <ConnectionStatus status={state.status} error={state.lastError} />
            <MetricsHud metrics={snapshot?.metrics ?? null} />
          </div>
          <div className="w-72">
            <OperatorPanel
              parameters={snapshot?.parameters ?? null}
              connected={connected}
            />
          </div>
        </div>

        <div className="flex items-end justify-between gap-4">
          <div className="w-96">
            <EventLog events={state.events} />
          </div>
          <div className="pointer-events-none rounded-lg bg-black/40 px-3 py-1.5 text-[10px] text-white/40 backdrop-blur">
            Forge · pure renderer + operator-control surface — no business rules run here
          </div>
        </div>
      </div>
    </main>
  );
}
