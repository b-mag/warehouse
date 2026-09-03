import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // React StrictMode intentionally double-mounts components in development. That is
  // incompatible with a single-WebGL-context renderer (R3F / three.js): the second mount
  // creates a new GL context while the first is still tearing down, and many drivers respond
  // by killing a context and refusing to restore it — the scene renders briefly, then goes
  // white ("context lost — attempting restore" with no "restored"). Disabling StrictMode
  // keeps the Canvas mounted exactly once. This only affects the dev double-invoke behavior;
  // production has never double-mounted.
  reactStrictMode: false,
};

export default nextConfig;