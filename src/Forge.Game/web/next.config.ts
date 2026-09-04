import type { NextConfig } from "next";

/** Engine origin for same-origin rewrites (avoids browser CORS on SignalR negotiate). */
const FORGE_ENGINE = (process.env.FORGE_API_PROXY ?? "http://localhost:5195").replace(
  /\/+$/,
  "",
);

const nextConfig: NextConfig = {
  // React StrictMode intentionally double-mounts components in development. That is
  // incompatible with a single-WebGL-context renderer (R3F / three.js): the second mount
  // creates a new GL context while the first is still tearing down, and many drivers respond
  // by killing a context and refusing to restore it — the scene renders briefly, then goes
  // white ("context lost — attempting restore" with no "restored"). Disabling StrictMode
  // keeps the Canvas mounted exactly once. This only affects the dev double-invoke behavior;
  // production has never double-mounted.
  reactStrictMode: false,

  // Proxy REST + SignalR through the Next origin so the browser never hits a cross-origin
  // negotiate (which shows up as "Failed to complete negotiation: Failed to fetch" when
  // the page origin is not in Forge:WebClientOrigins, e.g. localhost:3001).
  async rewrites() {
    return [
      { source: "/api/:path*", destination: `${FORGE_ENGINE}/api/:path*` },
      { source: "/hub/:path*", destination: `${FORGE_ENGINE}/hub/:path*` },
    ];
  },
};

export default nextConfig;
