/**
 * Runtime configuration for the Forge web client (task 37.1).
 *
 * The REST base URL and SignalR hub URL both derive from the headless engine base,
 * configurable via the `NEXT_PUBLIC_FORGE_API` environment variable and defaulting to
 * `http://localhost:5195`. `NEXT_PUBLIC_`-prefixed vars are inlined into the client
 * bundle by Next.js, so this resolves in the browser.
 */

export const FORGE_API_BASE = (
  process.env.NEXT_PUBLIC_FORGE_API ?? "http://localhost:5195"
).replace(/\/+$/, "");

/** REST endpoint paths, resolved against {@link FORGE_API_BASE}. */
export const ApiRoutes = {
  snapshot: `${FORGE_API_BASE}/api/query/snapshot`,
  operatorParameters: `${FORGE_API_BASE}/api/operator-parameters`,
  orders: `${FORGE_API_BASE}/api/orders`,
} as const;

/** The SignalR hub URL for the real-time channel (Req 23.1). */
export const SIGNALR_HUB_URL = `${FORGE_API_BASE}/hub/simulation`;
