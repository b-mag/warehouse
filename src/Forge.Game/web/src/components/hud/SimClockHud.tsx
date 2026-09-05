"use client";

/**
 * Authoritative simulated clock HUD — day index + HH:mm (24h) from engine Now.
 * Default sim-speed 60× means 1 wall-second = 1 sim-minute, so a "day" rolls
 * every 24 wall-minutes.
 */

import { useEffect, useState } from "react";

import { getMotion } from "@/lib/motionStore";

function formatSimClock(iso: string | null): { day: string; time: string } {
  if (!iso) {
    return { day: "—", time: "--:--" };
  }
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) {
    return { day: "—", time: "--:--" };
  }
  // Day index from sim midnight epochs (24 sim hours = 1 labeled day).
  const dayIndex = Math.floor(d.getTime() / 86_400_000);
  const hh = String(d.getUTCHours()).padStart(2, "0");
  const mm = String(d.getUTCMinutes()).padStart(2, "0");
  return { day: `Day ${dayIndex}`, time: `${hh}:${mm}` };
}

export function SimClockHud() {
  const [label, setLabel] = useState(() => formatSimClock(getMotion().simulatedAt));

  useEffect(() => {
    const id = window.setInterval(() => {
      setLabel(formatSimClock(getMotion().simulatedAt));
    }, 500);
    return () => window.clearInterval(id);
  }, []);

  return (
    <div className="rounded-lg border border-white/10 bg-black/60 px-3 py-2 backdrop-blur">
      <div className="text-[10px] uppercase tracking-wide text-amber-200/80">
        Warehouse clock
      </div>
      <div className="font-mono text-lg tabular-nums text-white">
        {label.time}
        <span className="ml-2 text-xs text-white/50">{label.day}</span>
      </div>
      <div className="text-[9px] leading-snug text-white/35">
        1 wall sec = 1 sim min · day rolls every 24 wall min @ 60×
      </div>
    </div>
  );
}
