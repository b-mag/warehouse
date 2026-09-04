"use client";

/**
 * React context provider that owns the SignalR connection and the authoritative
 * render-state store (task 37.1; Req 23.1, 23.3, 24.3, 24.9).
 *
 * On mount it:
 *  1. Fetches the REST snapshot as a cold seed so the view has state even before the
 *     hub delivers its own `Snapshot` (and as a fallback when the hub is unreachable).
 *  2. Opens a SignalR connection with automatic reconnect.
 *  3. Subscribes to the full-state `Snapshot` message and the incremental event
 *     messages by their exact engine names, dispatching each into the reducer store.
 *
 * The provider renders ONLY authoritative state and never computes business rules.
 */

import {
  HttpTransportType,
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import {
  createContext,
  useContext,
  useEffect,
  useReducer,
  useRef,
  type ReactNode,
} from "react";

import { fetchSnapshot } from "./api";
import { SIGNALR_HUB_URL } from "./config";
import type {
  BacklogChangedEvent,
  BlockedArrivalEvent,
  BlockedPlacementEvent,
  InventoryUpdateDto,
  LotExpiredEvent,
  OperatorParameterChangedEvent,
  PositionsUpdateDto,
  SimulationSnapshotDto,
  TemperatureExcursionEvent,
} from "./contracts";
import {
  forgeReducer,
  initialForgeState,
  type ForgeState,
} from "./store";

interface ForgeContextValue {
  state: ForgeState;
}

const ForgeContext = createContext<ForgeContextValue | null>(null);

/**
 * The exact SignalR client-method names the engine sends. `Snapshot` is the full-state
 * message (SimulationHub.SnapshotMethod, Req 23.3); the rest are incremental events
 * forwarded by SignalRStatePublisher.
 */
const HUB_METHODS = {
  Snapshot: "Snapshot",
  PositionsUpdate: "PositionsUpdate",
  InventoryUpdate: "InventoryUpdate",
  LotExpired: "LotExpired",
  TemperatureExcursion: "TemperatureExcursion",
  BlockedArrival: "BlockedArrival",
  BlockedPlacement: "BlockedPlacement",
  UnroutableTask: "UnroutableTask",
  DockBlocked: "DockBlocked",
  LoadingWindowClosed: "LoadingWindowClosed",
  BacklogChanged: "BacklogChanged",
  OperatorParameterChanged: "OperatorParameterChanged",
} as const;

export function ForgeProvider({ children }: { children: ReactNode }) {
  const [state, dispatch] = useReducer(forgeReducer, initialForgeState);
  const connectionRef = useRef<HubConnection | null>(null);

  useEffect(() => {
    let disposed = false;
    const abort = new AbortController();

    // 1. Cold REST seed so the view has authoritative state as early as possible and
    //    even if the hub is briefly unreachable (Req 23.3, graceful offline handling).
    void fetchSnapshot(abort.signal)
      .then((snapshot) => {
        if (!disposed) {
          dispatch({ type: "SNAPSHOT", snapshot });
        }
      })
      .catch((err: unknown) => {
        if (!disposed && !abort.signal.aborted) {
          dispatch({
            type: "ERROR",
            message: `Snapshot fetch failed: ${describeError(err)}`,
          });
        }
      });

    // 2. SignalR connection with automatic reconnect.
    // Same-origin hub URL (via Next rewrites) avoids CORS negotiate failures. Prefer
    // WebSockets, but allow SSE/LongPolling so the proxy path still works if WS upgrade
    // is unavailable.
    const connection = new HubConnectionBuilder()
      .withUrl(SIGNALR_HUB_URL, {
        transport:
          HttpTransportType.WebSockets |
          HttpTransportType.ServerSentEvents |
          HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
      .configureLogging(LogLevel.None)
      .build();
    connectionRef.current = connection;

    // 3. Subscribe to the full-state snapshot and every incremental event by name.
    connection.on(HUB_METHODS.Snapshot, (snapshot: SimulationSnapshotDto) => {
      dispatch({ type: "SNAPSHOT", snapshot });
    });
    connection.on(
      HUB_METHODS.PositionsUpdate,
      (update: PositionsUpdateDto) => {
        dispatch({ type: "POSITIONS_UPDATE", update });
      },
    );
    connection.on(
      HUB_METHODS.InventoryUpdate,
      (update: InventoryUpdateDto) => {
        dispatch({ type: "INVENTORY_UPDATE", update });
      },
    );
    connection.on(HUB_METHODS.LotExpired, (event: LotExpiredEvent) => {
      dispatch({ type: "LOT_EXPIRED", event });
    });
    connection.on(
      HUB_METHODS.TemperatureExcursion,
      (event: TemperatureExcursionEvent) => {
        dispatch({ type: "TEMPERATURE_EXCURSION", event });
      },
    );
    connection.on(HUB_METHODS.BlockedArrival, (event: BlockedArrivalEvent) => {
      dispatch({
        type: "EVENT_LOG",
        kind: "BlockedArrival",
        message: `Blocked arrival: ${event.reason}`,
      });
    });
    connection.on(
      HUB_METHODS.BlockedPlacement,
      (event: BlockedPlacementEvent) => {
        dispatch({
          type: "EVENT_LOG",
          kind: "BlockedPlacement",
          message: `Blocked placement: ${event.reason}`,
        });
      },
    );
    connection.on(HUB_METHODS.UnroutableTask, (payload: unknown) => {
      dispatch({
        type: "EVENT_LOG",
        kind: "UnroutableTask",
        message: `Unroutable task ${summarize(payload)}`,
      });
    });
    connection.on(HUB_METHODS.DockBlocked, (payload: unknown) => {
      dispatch({
        type: "EVENT_LOG",
        kind: "DockBlocked",
        message: `Dock blocked ${summarize(payload)}`,
      });
    });
    connection.on(HUB_METHODS.LoadingWindowClosed, (payload: unknown) => {
      dispatch({
        type: "EVENT_LOG",
        kind: "LoadingWindowClosed",
        message: `Loading window closed ${summarize(payload)}`,
      });
    });
    connection.on(HUB_METHODS.BacklogChanged, (event: BacklogChangedEvent) => {
      dispatch({ type: "BACKLOG_CHANGED", event });
    });
    connection.on(
      HUB_METHODS.OperatorParameterChanged,
      (event: OperatorParameterChangedEvent) => {
        dispatch({ type: "OPERATOR_PARAMETER_CHANGED", event });
      },
    );

    connection.onreconnecting(() => {
      dispatch({ type: "STATUS", status: "reconnecting" });
    });
    connection.onreconnected(() => {
      dispatch({ type: "STATUS", status: "connected" });
    });
    connection.onclose(() => {
      if (!disposed) {
        dispatch({ type: "STATUS", status: "disconnected" });
      }
    });

    const start = async () => {
      dispatch({ type: "STATUS", status: "connecting" });
      try {
        await connection.start();
        if (!disposed) {
          dispatch({ type: "STATUS", status: "connected" });
        }
      } catch (err) {
        // A start aborted by cleanup (StrictMode remount/unmount) is benign.
        if (disposed || isNegotiationAbort(err)) {
          return;
        }
        dispatch({ type: "STATUS", status: "disconnected" });
        dispatch({
          type: "ERROR",
          message: `Cannot reach engine at ${SIGNALR_HUB_URL}: ${describeError(err)}`,
        });
        // Retry the initial connection ourselves — automatic reconnect only covers
        // drops after a successful start (graceful offline handling).
        if (!disposed) {
          window.setTimeout(() => {
            if (!disposed) {
              void start();
            }
          }, 5000);
        }
      }
    };

    const startPromise = start();
    void startPromise;

    return () => {
      disposed = true;
      abort.abort();
      connectionRef.current = null;
      // Wait for any in-flight start() to settle BEFORE stopping, so we never abort negotiation.
      void startPromise
        .catch(() => undefined)
        .then(() =>
          connection.state !== HubConnectionState.Disconnected
            ? connection.stop()
            : undefined,
        )
        .catch(() => undefined);
    };
  }, []);

  return (
    <ForgeContext.Provider value={{ state }}>{children}</ForgeContext.Provider>
  );
}

/** Access the authoritative Forge render state. Must be used under a ForgeProvider. */
export function useForge(): ForgeState {
  const ctx = useContext(ForgeContext);
  if (!ctx) {
    throw new Error("useForge must be used within a ForgeProvider");
  }
  return ctx.state;
}

function isNegotiationAbort(err: unknown): boolean {
  const message = err instanceof Error ? err.message : String(err);
  return (
    message.includes("stopped during negotiation") ||
    message.includes("The connection was stopped")
  );
}

function describeError(err: unknown): string {
  if (err instanceof Error) {
    return err.message;
  }
  return String(err);
}

function summarize(payload: unknown): string {
  if (payload == null) {
    return "";
  }
  try {
    return JSON.stringify(payload);
  } catch {
    return String(payload);
  }
}
