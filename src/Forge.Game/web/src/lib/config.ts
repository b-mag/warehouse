/**
 * Runtime configuration for the Forge web client (task 37.1).
 *
 * By default the browser talks to the **same origin** (empty base). Next.js rewrites
 * `/api/*` and `/hub/*` to the headless engine (`http://localhost:5195`), which avoids
 * cross-origin SignalR negotiate failures.
 *
 * Set `NEXT_PUBLIC_FORGE_API` only when you need the browser to call the engine directly
 * (then Forge:WebClientOrigins must include the page origin).
 */

export const FORGE_API_BASE = (
  process.env.NEXT_PUBLIC_FORGE_API ?? ""
).replace(/\/+$/, "");

/** REST endpoint paths, resolved against {@link FORGE_API_BASE}. */
export const ApiRoutes = {
  snapshot: `${FORGE_API_BASE}/api/query/snapshot`,
  operatorParameters: `${FORGE_API_BASE}/api/operator-parameters`,
  orders: `${FORGE_API_BASE}/api/orders`,
} as const;

/** The SignalR hub URL for the real-time channel (Req 23.1). */
export const SIGNALR_HUB_URL = `${FORGE_API_BASE}/hub/simulation`;
