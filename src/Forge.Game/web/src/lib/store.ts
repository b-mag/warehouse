/**
 * Authoritative render-state store for the Forge web client (task 37.1; Req 24.9, 24.10).
 *
 * The store holds ONLY authoritative state received from the engine. It is:
 *  - seeded by the full `Snapshot` message (Req 23.3) and by the REST snapshot query, and
 *  - updated by incremental SignalR event messages.
 *
 * The reducer performs no business-rule computation. Incremental events carry state the
 * engine already decided (a lot expired, an excursion occurred, a backlog size changed,
 * the operator parameters changed); the reducer merely files that authoritative fact into
 * the last-known snapshot so the renderer can draw it. It never derives expiry, at-risk,
 * capacity, throughput, or any other rule locally (Req 24.9, 24.10, 2.4).
 */

import type {
  BacklogChangedEvent,
  LotExpiredEvent,
  OperatorParameterChangedEvent,
  SimulationSnapshotDto,
  TemperatureExcursionEvent,
} from "./contracts";

/** Connection lifecycle for the real-time channel, surfaced in the UI. */
export type ConnectionStatus =
  | "connecting"
  | "connected"
  | "reconnecting"
  | "disconnected";

/** A single engine-reported event, retained for a scrolling event log HUD. */
export interface EventLogEntry {
  id: number;
  kind: string;
  message: string;
  /** Wall-clock timestamp the client received the event. */
  receivedAt: number;
}

export interface ForgeState {
  status: ConnectionStatus;
  /** Last authoritative full-state snapshot, or null until first received. */
  snapshot: SimulationSnapshotDto | null;
  /** Most recent engine events (newest first), capped for the HUD. */
  events: EventLogEntry[];
  /** Last error surfaced from the Api or hub, cleared on success. */
  lastError: string | null;
  /** Monotonic counter bumped on every authoritative update to key event ids. */
  seq: number;
}

export const initialForgeState: ForgeState = {
  status: "connecting",
  snapshot: null,
  events: [],
  lastError: null,
  seq: 0,
};

const MAX_EVENTS = 100;

export type ForgeAction =
  | { type: "STATUS"; status: ConnectionStatus }
  | { type: "SNAPSHOT"; snapshot: SimulationSnapshotDto }
  | { type: "LOT_EXPIRED"; event: LotExpiredEvent }
  | { type: "TEMPERATURE_EXCURSION"; event: TemperatureExcursionEvent }
  | { type: "BACKLOG_CHANGED"; event: BacklogChangedEvent }
  | { type: "OPERATOR_PARAMETER_CHANGED"; event: OperatorParameterChangedEvent }
  | { type: "EVENT_LOG"; kind: string; message: string }
  | { type: "ERROR"; message: string | null };

function pushEvent(
  state: ForgeState,
  kind: string,
  message: string,
): EventLogEntry[] {
  const entry: EventLogEntry = {
    id: state.seq,
    kind,
    message,
    receivedAt: Date.now(),
  };
  return [entry, ...state.events].slice(0, MAX_EVENTS);
}

export function forgeReducer(
  state: ForgeState,
  action: ForgeAction,
): ForgeState {
  switch (action.type) {
    case "STATUS":
      return { ...state, status: action.status };

    case "SNAPSHOT":
      // Full-state replace: the snapshot is authoritative and supersedes any
      // incremental deltas applied since the last one (Req 23.3).
      return {
        ...state,
        snapshot: action.snapshot,
        lastError: null,
        seq: state.seq + 1,
      };

    case "LOT_EXPIRED": {
      const events = pushEvent(
        state,
        "LotExpired",
        `Lot ${short(action.event.lotId)} expired`,
      );
      if (!state.snapshot) {
        return { ...state, events, seq: state.seq + 1 };
      }
      // File the authoritative fact: the engine marked this lot expired.
      const lots = state.snapshot.lots.map((lot) =>
        lot.id === action.event.lotId
          ? { ...lot, isExpired: true, atRisk: true }
          : lot,
      );
      return {
        ...state,
        snapshot: { ...state.snapshot, lots },
        events,
        seq: state.seq + 1,
      };
    }

    case "TEMPERATURE_EXCURSION": {
      const events = pushEvent(
        state,
        "TemperatureExcursion",
        `Excursion on lot ${short(action.event.lotId)} @ ${action.event.celsius}°C`,
      );
      if (!state.snapshot) {
        return { ...state, events, seq: state.seq + 1 };
      }
      // The engine flagged this lot at-risk; reflect the reported flag only.
      const lots = state.snapshot.lots.map((lot) =>
        lot.id === action.event.lotId ? { ...lot, atRisk: true } : lot,
      );
      return {
        ...state,
        snapshot: { ...state.snapshot, lots },
        events,
        seq: state.seq + 1,
      };
    }

    case "BACKLOG_CHANGED": {
      const events = pushEvent(
        state,
        "BacklogChanged",
        `${action.event.kind} backlog → ${action.event.newSize}`,
      );
      if (!state.snapshot) {
        return { ...state, events, seq: state.seq + 1 };
      }
      // The engine reports which backlog changed and its new size; store it verbatim.
      const kind = action.event.kind.toLowerCase();
      const metrics = { ...state.snapshot.metrics };
      if (kind.includes("receiv")) {
        metrics.receiving = action.event.newSize;
      } else if (kind.includes("outbound")) {
        metrics.outbound = action.event.newSize;
      }
      return {
        ...state,
        snapshot: { ...state.snapshot, metrics },
        events,
        seq: state.seq + 1,
      };
    }

    case "OPERATOR_PARAMETER_CHANGED": {
      const events = pushEvent(
        state,
        "OperatorParameterChanged",
        `Parameters updated (strategy: ${action.event.state.slottingStrategy})`,
      );
      if (!state.snapshot) {
        return { ...state, events, seq: state.seq + 1 };
      }
      // Converge on the engine-echoed authoritative parameter state (Req 20.9).
      return {
        ...state,
        snapshot: { ...state.snapshot, parameters: action.event.state },
        events,
        seq: state.seq + 1,
      };
    }

    case "EVENT_LOG":
      return {
        ...state,
        events: pushEvent(state, action.kind, action.message),
        seq: state.seq + 1,
      };

    case "ERROR":
      return { ...state, lastError: action.message };

    default:
      return state;
  }
}

function short(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id;
}
