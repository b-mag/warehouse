"use client";

/**
 * Connection status banner (task 37.1 graceful-offline handling).
 *
 * Surfaces the real-time channel lifecycle so the operator sees "connecting to engine…"
 * or a disconnected state rather than a blank/crashed view when the engine at :5195 is
 * offline. Reconnection is automatic; this only reports.
 */

import type { ConnectionStatus as Status } from "@/lib/store";

const LABEL: Record<Status, string> = {
  connecting: "Connecting to engine…",
  connected: "Connected",
  reconnecting: "Reconnecting…",
  disconnected: "Engine offline — retrying…",
};

const DOT: Record<Status, string> = {
  connecting: "bg-amber-400 animate-pulse",
  connected: "bg-emerald-400",
  reconnecting: "bg-amber-400 animate-pulse",
  disconnected: "bg-red-500",
};

export function ConnectionStatus({
  status,
  error,
}: {
  status: Status;
  error: string | null;
}) {
  return (
    <div className="pointer-events-auto flex items-center gap-2 rounded-lg border border-white/10 bg-black/60 px-3 py-1.5 backdrop-blur">
      <span className={`h-2.5 w-2.5 rounded-full ${DOT[status]}`} />
      <span className="text-xs font-medium text-white">{LABEL[status]}</span>
      {error && status !== "connected" && (
        <span
          className="ml-1 max-w-[18rem] truncate text-[10px] text-white/40"
          title={error}
        >
          {error}
        </span>
      )}
    </div>
  );
}
