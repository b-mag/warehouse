"use client";

/**
 * The pseudo-3D top-down / slight-isometric operations view (task 37.2; Req 24.2, 24.3).
 *
 * Composes the authoritative snapshot into an R3F scene: temperature-zone floors, gel-lot
 * cubes, moving agents, and docked starships. The camera is a fixed slight-isometric
 * overhead angle (RollerCoaster-Tycoon style); OrbitControls allow basic orbit/zoom/pan
 * but are constrained so the camera can never flip to a degenerate edge-on/profile view.
 * Everything drawn comes from the authoritative DTOs — the view computes no rules.
 */

import { OrbitControls } from "@react-three/drei";
import { Canvas } from "@react-three/fiber";
import { memo, useCallback, useMemo, useRef, useState } from "react";

import type { SimulationSnapshotDto } from "@/lib/contracts";
import {
  ZONE_SIZE,
  groupLotsByZone,
  layoutZones,
} from "@/lib/layout";

import { Agents } from "./Agents";
import { Lots } from "./Lots";
import { Starships } from "./Starships";
import { Zones } from "./Zones";

interface OperationsViewProps {
  snapshot: SimulationSnapshotDto;
}

/**
 * Only the SPATIAL parts of the snapshot drive the scene. Metrics / operator-parameter / event
 * updates arrive far more frequently (roughly every tick) but do not change what is drawn, so we
 * memoize on the spatial array identities. The reducer keeps zones/lots/agents/starships references
 * stable across a metrics-only update, so this prevents the scene from rebuilding — and its GPU
 * resources from churning — dozens of times a second (which was losing the WebGL context).
 */
function SceneCanvas({
  snapshot,
  onContextLost,
  onContextRestored,
}: OperationsViewProps & {
  onContextLost: () => void;
  onContextRestored: () => void;
}) {
  const { zones, lots, agents, starships } = snapshot;

  const placements = useMemo(() => layoutZones(zones), [zones]);
  const lotsByZone = useMemo(() => groupLotsByZone(lots), [lots]);

  // Front docking edge derived from the zone grid extent.
  const edgeZ = useMemo(() => {
    let maxZ = 0;
    for (const p of placements) {
      maxZ = Math.max(maxZ, p.centerZ);
    }
    return maxZ + ZONE_SIZE * 1.5;
  }, [placements]);

  return (
    <Canvas
      // NOTE: shadows are intentionally OFF. Shadow maps are a heavy GPU allocation and a common
      // trigger for "context lost" on page refresh (the previous context is not always torn down
      // before the new one is created). The readable operations view does not need them.
      dpr={[1, 1.5]}
      className="h-full w-full"
      // A perspective camera at a fixed tycoon-style overhead-ish angle. Perspective (not
      // orthographic-with-negative-near) avoids the projection edge cases that flipped the view to a
      // profile. We do NOT request "high-performance" powerPreference: forcing a GPU switch is itself
      // a context-loss trigger on multi-GPU machines. antialias off keeps the context lighter.
      camera={{ position: [40, 44, 46], fov: 35, near: 0.1, far: 2000 }}
      gl={{ antialias: false, powerPreference: "default", failIfMajorPerformanceCaveat: false }}
      onCreated={({ gl }) => {
        const canvas = gl.domElement;
        canvas.addEventListener(
          "webglcontextlost",
          (e) => {
            // Tell the browser we intend to restore so it keeps the canvas recoverable.
            e.preventDefault();
            console.warn("[Forge scene] WebGL context lost — attempting restore.");
            onContextLost();
          },
          false,
        );
        canvas.addEventListener(
          "webglcontextrestored",
          () => {
            console.info("[Forge scene] WebGL context restored.");
            onContextRestored();
          },
          false,
        );
      }}
    >
      <color attach="background" args={["#0b0f14"]} />

      <OrbitControls
        makeDefault
        enablePan
        enableZoom
        enableRotate
        target={[0, 0, 0]}
        // Keep the camera above the floor: never allow it to drop to (or past) horizontal,
        // which is what produced the "profile view" before the crash.
        minPolarAngle={0.15}
        maxPolarAngle={Math.PI / 2 - 0.15}
        minDistance={20}
        maxDistance={220}
      />

      <ambientLight intensity={0.8} />
      <directionalLight position={[30, 50, 20]} intensity={1.1} />
      <hemisphereLight args={["#9fb8d6", "#20252e", 0.4]} />

      <Zones placements={placements} lotsByZone={lotsByZone} />
      <Lots placements={placements} lotsByZone={lotsByZone} />
      <Agents agents={agents} />
      <Starships starships={starships} edgeZ={edgeZ} />
    </Canvas>
  );
}

/**
 * Owns active context-loss recovery. A lost WebGL context does NOT throw a React error, so an
 * error boundary can't catch it — the canvas just goes white. When the browser fires
 * `webglcontextlost` and does NOT follow with `webglcontextrestored` shortly after, we force a
 * full remount of the Canvas by bumping a React key. Remounting builds a brand-new GL context
 * from scratch, which reliably recovers even on drivers that refuse the browser's own restore.
 */
function OperationsViewImpl({ snapshot }: OperationsViewProps) {
  const [canvasKey, setCanvasKey] = useState(0);
  const restoreTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const handleContextLost = useCallback(() => {
    if (restoreTimer.current) {
      clearTimeout(restoreTimer.current);
    }
    // Give the browser a brief window to restore on its own; if it doesn't, force a remount.
    restoreTimer.current = setTimeout(() => {
      console.warn("[Forge scene] context not restored — remounting canvas.");
      setCanvasKey((k) => k + 1);
      restoreTimer.current = null;
    }, 750);
  }, []);

  const handleContextRestored = useCallback(() => {
    if (restoreTimer.current) {
      clearTimeout(restoreTimer.current);
      restoreTimer.current = null;
    }
  }, []);

  return (
    <SceneCanvas
      key={canvasKey}
      snapshot={snapshot}
      onContextLost={handleContextLost}
      onContextRestored={handleContextRestored}
    />
  );
}

/**
 * Memoized so the scene only re-renders when a spatial array actually changes identity — not on the
 * frequent metrics/parameter/event updates that share the same spatial references.
 */
export const OperationsView = memo(
  OperationsViewImpl,
  (prev, next) =>
    prev.snapshot.zones === next.snapshot.zones &&
    prev.snapshot.lots === next.snapshot.lots &&
    prev.snapshot.agents === next.snapshot.agents &&
    prev.snapshot.starships === next.snapshot.starships,
);