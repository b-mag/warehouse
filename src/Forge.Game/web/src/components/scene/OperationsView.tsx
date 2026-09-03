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
import { useMemo } from "react";

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
  /** Store sequence number; bumped on each authoritative update to trigger agent snap. */
  snapSeq: number;
}

export function OperationsView({ snapshot, snapSeq }: OperationsViewProps) {
  const placements = useMemo(
    () => layoutZones(snapshot.zones),
    [snapshot.zones],
  );
  const lotsByZone = useMemo(
    () => groupLotsByZone(snapshot.lots),
    [snapshot.lots],
  );

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
      shadows
      dpr={[1, 2]}
      className="h-full w-full"
      // A perspective camera at a fixed tycoon-style overhead-ish angle. Perspective (not
      // orthographic-with-negative-near) avoids the projection edge cases that flipped the
      // view to a profile. gl defaults are fine; powerPreference hints the discrete GPU.
      camera={{ position: [40, 44, 46], fov: 35, near: 0.1, far: 2000 }}
      gl={{ antialias: true, powerPreference: "high-performance" }}
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

      <ambientLight intensity={0.7} />
      <directionalLight
        position={[30, 50, 20]}
        intensity={1.1}
        castShadow
        shadow-mapSize-width={1024}
        shadow-mapSize-height={1024}
      />
      <hemisphereLight args={["#9fb8d6", "#20252e", 0.4]} />

      <Zones placements={placements} lotsByZone={lotsByZone} />
      <Lots placements={placements} lotsByZone={lotsByZone} />
      <Agents agents={snapshot.agents} snapSeq={snapSeq} />
      <Starships starships={snapshot.starships} edgeZ={edgeZ} />
    </Canvas>
  );
}