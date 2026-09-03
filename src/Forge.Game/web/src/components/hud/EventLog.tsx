"use client";

/**
 * Scrolling event-log HUD (task 37.2/37.3 observability).
 *
 * Shows the most recent authoritative engine events (expiries, excursions, blocked
 * arrivals/placements, backlog changes, parameter changes) so disruptions are visible
 * as they happen. Purely a readout of received events.
 */

import type { EventLogEntry } from "@/lib/store";

const KIND_COLOR: Record<string, string> = {
  LotExpired: "text-red-300",
  TemperatureExcursion: "text-amber-300",
  BlockedArrival: "text-orange-300",
  BlockedPlacement: "text-orange-300",
  UnroutableTask: "text-orange-300",
  DockBlocked: "text-orange-300",
  LoadingWindowClosed: "text-sky-300",
  BacklogChanged: "text-sky-200",
  OperatorParameterChanged: "text-emerald-300",
};

export function EventLog({ events }: { events: EventLogEntry[] }) {
  return (
    <section className="pointer-events-auto flex max-h-56 flex-col rounded-lg border border-white/10 bg-black/60 p-3 backdrop-blur">
      <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-sky-300">
        Event Feed
      </h2>
      {events.length === 0 ? (
        <p className="text-xs text-white/40">No events yet.</p>
      ) : (
        <ul className="flex flex-col gap-1 overflow-y-auto pr-1">
          {events.map((e) => (
            <li key={e.id} className="flex gap-2 text-[11px] leading-tight">
              <span className={`font-semibold ${KIND_COLOR[e.kind] ?? "text-white/70"}`}>
                {e.kind}
              </span>
              <span className="text-white/60">{e.message}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
