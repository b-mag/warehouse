"use client";

/**
 * React context provider that owns the SignalR connection and the authoritative
 * render-state store (task 37.1; Req 23.1, 23.3, 24.3, 24.9).
 *
 * High-frequency PositionsUpdate traffic is written to motionStore (not the React
 * reducer) so the Dashboard / R3F tree does not reconcile ~5×/sec.
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
import { seedMotionFromSnapshot, setMotionFromPositions } from "./motionStore";
import {
  forgeReducer,
  initialForgeState,
  type ForgeState,
} from "./store";

interface ForgeContextValue {
  state: ForgeState;
}

const ForgeContext = createContext<ForgeContextValue | null>(null);

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
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    const abort = new AbortController();

    void fetchSnapshot(abort.signal)
      .then((snapshot) => {
        if (!disposed) {
          seedMotionFromSnapshot(snapshot);
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

    const onSnapshot = (snapshot: SimulationSnapshotDto) => {
      seedMotionFromSnapshot(snapshot);
      dispatch({ type: "SNAPSHOT", snapshot });
    };
    const onPositions = (update: PositionsUpdateDto) => {
      setMotionFromPositions(update);
    };
    const onInventory = (update: InventoryUpdateDto) => {
      dispatch({ type: "INVENTORY_UPDATE", update });
    };
    const onLotExpired = (event: LotExpiredEvent) => {
      dispatch({ type: "LOT_EXPIRED", event });
    };
    const onTemp = (event: TemperatureExcursionEvent) => {
      dispatch({ type: "TEMPERATURE_EXCURSION", event });
    };
    const onBlockedArrival = (event: BlockedArrivalEvent) => {
      dispatch({
        type: "EVENT_LOG",
        kind: "BlockedArrival",
        message: `Blocked arrival: ${event.reason}`,
      });
    };
    const onBlockedPlacement = (event: BlockedPlacementEvent) => {
      dispatch({
        type: "EVENT_LOG",
        kind: "BlockedPlacement",
        message: `Blocked placement: ${event.reason}`,
      });
    };
    const onUnroutable = (payload: unknown) => {
      dispatch({
        type: "EVENT_LOG",
        kind: "UnroutableTask",
        message: `Unroutable task ${summarize(payload)}`,
      });
    };
    const onDockBlocked = (payload: unknown) => {
      dispatch({
        type: "EVENT_LOG",
        kind: "DockBlocked",
        message: `Dock blocked ${summarize(payload)}`,
      });
    };
    const onLoadingClosed = (payload: unknown) => {
      dispatch({
        type: "EVENT_LOG",
        kind: "LoadingWindowClosed",
        message: `Loading window closed ${summarize(payload)}`,
      });
    };
    const onBacklog = (event: BacklogChangedEvent) => {
      dispatch({ type: "BACKLOG_CHANGED", event });
    };
    const onParams = (event: OperatorParameterChangedEvent) => {
      dispatch({ type: "OPERATOR_PARAMETER_CHANGED", event });
    };

    connection.on(HUB_METHODS.Snapshot, onSnapshot);
    connection.on(HUB_METHODS.PositionsUpdate, onPositions);
    connection.on(HUB_METHODS.InventoryUpdate, onInventory);
    connection.on(HUB_METHODS.LotExpired, onLotExpired);
    connection.on(HUB_METHODS.TemperatureExcursion, onTemp);
    connection.on(HUB_METHODS.BlockedArrival, onBlockedArrival);
    connection.on(HUB_METHODS.BlockedPlacement, onBlockedPlacement);
    connection.on(HUB_METHODS.UnroutableTask, onUnroutable);
    connection.on(HUB_METHODS.DockBlocked, onDockBlocked);
    connection.on(HUB_METHODS.LoadingWindowClosed, onLoadingClosed);
    connection.on(HUB_METHODS.BacklogChanged, onBacklog);
    connection.on(HUB_METHODS.OperatorParameterChanged, onParams);

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
        if (disposed || isNegotiationAbort(err)) {
          return;
        }
        dispatch({ type: "STATUS", status: "disconnected" });
        dispatch({
          type: "ERROR",
          message: `Cannot reach engine at ${SIGNALR_HUB_URL}: ${describeError(err)}`,
        });
        if (!disposed) {
          reconnectTimer = setTimeout(() => {
            reconnectTimer = null;
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
      if (reconnectTimer) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
      }
      connectionRef.current = null;

      connection.off(HUB_METHODS.Snapshot, onSnapshot);
      connection.off(HUB_METHODS.PositionsUpdate, onPositions);
      connection.off(HUB_METHODS.InventoryUpdate, onInventory);
      connection.off(HUB_METHODS.LotExpired, onLotExpired);
      connection.off(HUB_METHODS.TemperatureExcursion, onTemp);
      connection.off(HUB_METHODS.BlockedArrival, onBlockedArrival);
      connection.off(HUB_METHODS.BlockedPlacement, onBlockedPlacement);
      connection.off(HUB_METHODS.UnroutableTask, onUnroutable);
      connection.off(HUB_METHODS.DockBlocked, onDockBlocked);
      connection.off(HUB_METHODS.LoadingWindowClosed, onLoadingClosed);
      connection.off(HUB_METHODS.BacklogChanged, onBacklog);
      connection.off(HUB_METHODS.OperatorParameterChanged, onParams);

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
