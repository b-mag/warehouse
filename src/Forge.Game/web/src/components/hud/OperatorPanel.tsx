"use client";

/**
 * Operator control panel — the six live parameters, wired THROUGH the Api
 * (task 37.3; Req 24.4, 24.5, 24.6).
 *
 * The panel reads the current authoritative values from the snapshot's parameters and
 * PUTs a single `{ key, value }` change per control. It does NOT apply the change
 * locally: it issues the command and lets the engine's echoed `OperatorParameterChanged`
 * (and the PUT response) converge the store (Req 20.9, 24.5). All six parameters map to
 * the canonical OperatorParameterKey values; slotting strategy uses the exact
 * SlottingStrategyKey strings the engine validates.
 */

import { useState } from "react";

import { ForgeApiError, updateOperatorParameter } from "@/lib/api";
import {
  OperatorParameterKey,
  SlottingStrategyKey,
  type OperatorParameterStateDto,
} from "@/lib/contracts";

interface OperatorPanelProps {
  /** Authoritative parameter state from the snapshot, or null before first snapshot. */
  parameters: OperatorParameterStateDto | null;
  /** Whether the client is connected to the engine (controls are disabled if not). */
  connected: boolean;
}

type Pending = Partial<Record<string, boolean>>;

export function OperatorPanel({ parameters, connected }: OperatorPanelProps) {
  const [pending, setPending] = useState<Pending>({});
  const [error, setError] = useState<string | null>(null);
  // Local draft values for numeric inputs so typing doesn't fight the authoritative echo.
  const [draft, setDraft] = useState<Record<string, string>>({});
  // Track the last authoritative params we seeded from, in state (the blessed React
  // pattern for storing information from previous renders). When the engine echoes a new
  // parameter object we re-seed the draft during render — before paint, no extra flash.
  const [seededFrom, setSeededFrom] =
    useState<OperatorParameterStateDto | null>(null);

  if (parameters && parameters !== seededFrom) {
    setSeededFrom(parameters);
    setDraft({
      [OperatorParameterKey.SimSpeed]: String(parameters.simSpeed),
      [OperatorParameterKey.WorkersOnShift]: String(parameters.workersOnShift),
      [OperatorParameterKey.OpenDockBays]: String(parameters.openDockBays),
      [OperatorParameterKey.InboundRate]: String(parameters.inboundRate),
      [OperatorParameterKey.DemandMultiplier]: String(
        parameters.demandMultiplier,
      ),
    });
  }

  const submit = async (key: string, value: string) => {
    setPending((p) => ({ ...p, [key]: true }));
    setError(null);
    try {
      // Fire the command at the Api; the store converges on the SignalR echo.
      await updateOperatorParameter({ key, value });
    } catch (err) {
      if (err instanceof ForgeApiError) {
        setError(err.body || err.message);
      } else if (err instanceof Error) {
        setError(err.message);
      } else {
        setError(String(err));
      }
    } finally {
      setPending((p) => ({ ...p, [key]: false }));
    }
  };

  const disabled = !connected || !parameters;

  return (
    <section className="pointer-events-auto rounded-lg border border-white/10 bg-black/60 p-3 backdrop-blur">
      <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-sky-300">
        Operator Controls
      </h2>

      {!parameters ? (
        <p className="text-xs text-white/40">Awaiting parameters…</p>
      ) : (
        <div className="flex flex-col gap-3">
          <NumberControl
            label="Simulation speed"
            hint="0 = paused, 1 = real-time, >1 = accelerated"
            paramKey={OperatorParameterKey.SimSpeed}
            step={0.25}
            min={0}
            value={draft[OperatorParameterKey.SimSpeed] ?? ""}
            pending={!!pending[OperatorParameterKey.SimSpeed]}
            disabled={disabled}
            onChange={(v) =>
              setDraft((d) => ({ ...d, [OperatorParameterKey.SimSpeed]: v }))
            }
            onCommit={(v) => submit(OperatorParameterKey.SimSpeed, v)}
          />
          <NumberControl
            label="Workers on shift"
            hint="raise until throughput plateaus"
            paramKey={OperatorParameterKey.WorkersOnShift}
            step={1}
            min={0}
            value={draft[OperatorParameterKey.WorkersOnShift] ?? ""}
            pending={!!pending[OperatorParameterKey.WorkersOnShift]}
            disabled={disabled}
            onChange={(v) =>
              setDraft((d) => ({
                ...d,
                [OperatorParameterKey.WorkersOnShift]: v,
              }))
            }
            onCommit={(v) => submit(OperatorParameterKey.WorkersOnShift, v)}
          />
          <NumberControl
            label="Open dock bays"
            paramKey={OperatorParameterKey.OpenDockBays}
            step={1}
            min={0}
            value={draft[OperatorParameterKey.OpenDockBays] ?? ""}
            pending={!!pending[OperatorParameterKey.OpenDockBays]}
            disabled={disabled}
            onChange={(v) =>
              setDraft((d) => ({
                ...d,
                [OperatorParameterKey.OpenDockBays]: v,
              }))
            }
            onCommit={(v) => submit(OperatorParameterKey.OpenDockBays, v)}
          />
          <NumberControl
            label="Inbound arrival rate"
            paramKey={OperatorParameterKey.InboundRate}
            step={0.5}
            min={0}
            value={draft[OperatorParameterKey.InboundRate] ?? ""}
            pending={!!pending[OperatorParameterKey.InboundRate]}
            disabled={disabled}
            onChange={(v) =>
              setDraft((d) => ({ ...d, [OperatorParameterKey.InboundRate]: v }))
            }
            onCommit={(v) => submit(OperatorParameterKey.InboundRate, v)}
          />
          <NumberControl
            label="Colony demand multiplier"
            paramKey={OperatorParameterKey.DemandMultiplier}
            step={0.25}
            min={0}
            value={draft[OperatorParameterKey.DemandMultiplier] ?? ""}
            pending={!!pending[OperatorParameterKey.DemandMultiplier]}
            disabled={disabled}
            onChange={(v) =>
              setDraft((d) => ({
                ...d,
                [OperatorParameterKey.DemandMultiplier]: v,
              }))
            }
            onCommit={(v) => submit(OperatorParameterKey.DemandMultiplier, v)}
          />

          <label className="flex flex-col gap-1">
            <span className="text-[10px] uppercase tracking-wide text-white/50">
              Slotting strategy
            </span>
            <select
              className="rounded border border-white/15 bg-white/5 px-2 py-1 text-sm text-white disabled:opacity-40"
              value={parameters.slottingStrategy}
              disabled={disabled || !!pending[OperatorParameterKey.SlottingStrategy]}
              onChange={(e) =>
                submit(OperatorParameterKey.SlottingStrategy, e.target.value)
              }
            >
              <option value={SlottingStrategyKey.VelocityAffinity}>
                Velocity-affinity
              </option>
              <option value={SlottingStrategyKey.NaiveFirstAvailable}>
                Naive first-available
              </option>
            </select>
          </label>
        </div>
      )}

      {error && (
        <p className="mt-2 rounded border border-red-500/40 bg-red-500/10 px-2 py-1 text-xs text-red-300">
          {error}
        </p>
      )}
    </section>
  );
}

interface NumberControlProps {
  label: string;
  hint?: string;
  paramKey: string;
  step: number;
  min: number;
  value: string;
  pending: boolean;
  disabled: boolean;
  onChange: (value: string) => void;
  onCommit: (value: string) => void;
}

function NumberControl({
  label,
  hint,
  step,
  min,
  value,
  pending,
  disabled,
  onChange,
  onCommit,
}: NumberControlProps) {
  return (
    <label className="flex flex-col gap-1">
      <span className="flex items-center justify-between text-[10px] uppercase tracking-wide text-white/50">
        {label}
        {pending && <span className="text-sky-300">…</span>}
      </span>
      <div className="flex items-center gap-1">
        <input
          type="number"
          inputMode="decimal"
          className="w-full rounded border border-white/15 bg-white/5 px-2 py-1 font-mono text-sm text-white disabled:opacity-40"
          step={step}
          min={min}
          value={value}
          disabled={disabled || pending}
          onChange={(e) => onChange(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              (e.target as HTMLInputElement).blur();
            }
          }}
          onBlur={(e) => {
            const v = e.target.value.trim();
            if (v !== "") {
              onCommit(v);
            }
          }}
        />
      </div>
      {hint && <span className="text-[9px] text-white/30">{hint}</span>}
    </label>
  );
}
