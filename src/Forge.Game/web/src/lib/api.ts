/**
 * REST client for the Forge headless engine Api (task 37.1; Req 24.5, 24.6).
 *
 * Every operator command travels THROUGH the Api — the client issues requests and
 * renders authoritative responses, computing no business rules (Req 24.5, 24.9).
 * Non-200 responses surface the error body so the UI can display the engine's own
 * rejection reason (e.g. an out-of-range operator parameter, Req 20.8).
 */

import { ApiRoutes } from "./config";
import type {
  CreateColonyOrderRequest,
  CreateColonyOrderResponse,
  OperatorParameterDto,
  OperatorParameterStateDto,
  SimulationSnapshotDto,
} from "./contracts";

/** Error thrown when the engine returns a non-2xx status; carries the surfaced body. */
export class ForgeApiError extends Error {
  readonly status: number;
  readonly body: string;

  constructor(status: number, body: string) {
    super(`Forge API error ${status}: ${body || "(no body)"}`);
    this.name = "ForgeApiError";
    this.status = status;
    this.body = body;
  }
}

async function ensureOk(response: Response): Promise<void> {
  if (response.ok) {
    return;
  }
  // Surface the engine's error body verbatim (Req 20.8 / error handling).
  let body = "";
  try {
    body = await response.text();
  } catch {
    body = "";
  }
  throw new ForgeApiError(response.status, body);
}

/** GET {base}/api/query/snapshot → SimulationSnapshotDto (read-only, Req 9.3). */
export async function fetchSnapshot(
  signal?: AbortSignal,
): Promise<SimulationSnapshotDto> {
  const response = await fetch(ApiRoutes.snapshot, {
    method: "GET",
    headers: { Accept: "application/json" },
    cache: "no-store",
    signal,
  });
  await ensureOk(response);
  return (await response.json()) as SimulationSnapshotDto;
}

/** GET {base}/api/operator-parameters → OperatorParameterStateDto (Req 20.1). */
export async function fetchOperatorParameters(
  signal?: AbortSignal,
): Promise<OperatorParameterStateDto> {
  const response = await fetch(ApiRoutes.operatorParameters, {
    method: "GET",
    headers: { Accept: "application/json" },
    cache: "no-store",
    signal,
  });
  await ensureOk(response);
  return (await response.json()) as OperatorParameterStateDto;
}

/**
 * PUT {base}/api/operator-parameters with body `{ key, value }` (Req 20.8, 20.9).
 * On success the engine returns the updated full parameter state and also publishes
 * `OperatorParameterChanged` over SignalR so all clients converge.
 */
export async function updateOperatorParameter(
  change: OperatorParameterDto,
  signal?: AbortSignal,
): Promise<OperatorParameterStateDto> {
  const response = await fetch(ApiRoutes.operatorParameters, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      Accept: "application/json",
    },
    body: JSON.stringify(change),
    signal,
  });
  await ensureOk(response);
  return (await response.json()) as OperatorParameterStateDto;
}

/** POST {base}/api/orders — create a colony order (Req 9.1). */
export async function createColonyOrder(
  request: CreateColonyOrderRequest,
  signal?: AbortSignal,
): Promise<CreateColonyOrderResponse> {
  const response = await fetch(ApiRoutes.orders, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Accept: "application/json",
    },
    body: JSON.stringify(request),
    signal,
  });
  await ensureOk(response);
  return (await response.json()) as CreateColonyOrderResponse;
}
