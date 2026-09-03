"use client";

/**
 * Live metrics HUD — the "watch the bottleneck" surface (task 37.3; Req 24.4).
 *
 * Renders the authoritative BacklogMetricsDto: receiving/outbound backlog, inbound/
 * outbound throughput, dock contention and utilization. Values come straight from the
 * snapshot; nothing is derived here.
 */

import type { BacklogMetricsDto } from "@/lib/contracts";

interface MetricsHudProps {
  metrics: BacklogMetricsDto | null;
}

interface Stat {
  label: string;
  value: string;
  hint?: string;
}

function formatNumber(value: number, digits = 1): string {
  if (!Number.isFinite(value)) {
    return "—";
  }
  return value.toFixed(digits);
}

export function MetricsHud({ metrics }: MetricsHudProps) {
  const stats: Stat[] = metrics
    ? [
        { label: "Receiving backlog", value: String(metrics.receiving) },
        { label: "Outbound backlog", value: String(metrics.outbound) },
        {
          label: "Inbound throughput",
          value: formatNumber(metrics.inboundThroughput),
          hint: "lots / unit time",
        },
        {
          label: "Outbound throughput",
          value: formatNumber(metrics.outboundThroughput),
          hint: "lots / unit time",
        },
        { label: "Dock contention", value: String(metrics.dockContention) },
        {
          label: "Dock utilization",
          value: `${formatNumber(metrics.dockUtilization * 100, 0)}%`,
        },
      ]
    : [];

  return (
    <section className="pointer-events-auto rounded-lg border border-white/10 bg-black/60 p-3 backdrop-blur">
      <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-sky-300">
        Live Metrics
      </h2>
      {metrics ? (
        <dl className="grid grid-cols-2 gap-x-4 gap-y-2">
          {stats.map((stat) => (
            <div key={stat.label} className="flex flex-col">
              <dt className="text-[10px] uppercase tracking-wide text-white/50">
                {stat.label}
              </dt>
              <dd className="font-mono text-lg leading-tight text-white">
                {stat.value}
              </dd>
              {stat.hint && (
                <span className="text-[9px] text-white/30">{stat.hint}</span>
              )}
            </div>
          ))}
        </dl>
      ) : (
        <p className="text-xs text-white/40">Awaiting snapshot…</p>
      )}
    </section>
  );
}
