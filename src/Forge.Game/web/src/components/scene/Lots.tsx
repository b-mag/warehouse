"use client";

/**
 * Gel lots as extruded cubes grouped into their zone footprint (task 37.2; Req 24.2).
 *
 * PERFORMANCE / STABILITY: the sim streams frequent snapshots and the page may be refreshed while a
 * context still holds resources. To keep the WebGL context healthy, each zone draws its cubes as up
 * to three INSTANCED meshes (one per lot state). All cubes of a state share a single geometry +
 * material and render in ONE draw call, with per-cube position/height set via instance matrices — so
 * a snapshot update allocates NO new GPU objects and there are no module-level GPU singletons that
 * could outlive their context across a refresh. Color reflects the authoritative lot state; nothing
 * is derived here.
 */

import { Text } from "@react-three/drei";
import { memo, useLayoutEffect, useMemo, useRef } from "react";
import { InstancedMesh, Object3D } from "three";

import type { GelLotDto } from "@/lib/contracts";
import { ZONE_SIZE, type ZonePlacement } from "@/lib/layout";

const CUBE = 0.9;
const FLOOR_TOP = 0.4;
const PAD = 1.0;

/** Max cubes drawn per zone. Beyond this, the zone count label conveys the rest. */
const MAX_CUBES_PER_ZONE = 36;

type LotState = "normal" | "atRisk" | "expired";

const STATE_COLOR: Record<LotState, string> = {
  normal: "#dfe6ee",
  atRisk: "#e0a53a",
  expired: "#d13b3b",
};

function lotState(lot: GelLotDto): LotState {
  if (lot.isExpired) return "expired";
  if (lot.atRisk) return "atRisk";
  return "normal";
}

/** Height multiplier from quantity (clamped so the view stays readable). */
function stackHeight(quantity: number): number {
  const h = 0.6 + Math.log10(Math.max(1, quantity)) * 0.8;
  return Math.min(h, 3.2);
}

/** Order lots so problem lots (expired, then at-risk) survive the per-zone cap, then cap. */
function prioritizeAndCap(lots: GelLotDto[]): GelLotDto[] {
  const sorted = [...lots].sort((a, b) => {
    const rank = (l: GelLotDto) => (l.isExpired ? 0 : l.atRisk ? 1 : 2);
    return rank(a) - rank(b);
  });
  return sorted.slice(0, MAX_CUBES_PER_ZONE);
}

interface CubePlacement {
  x: number;
  z: number;
  height: number;
}

/**
 * One instanced draw for all cubes of a single state: shared geometry + material, per-cube transform
 * via instance matrices. Allocates no new GPU objects on snapshot changes.
 */
function InstancedCubes({
  cubes,
  color,
}: {
  cubes: CubePlacement[];
  color: string;
}) {
  const meshRef = useRef<InstancedMesh>(null);
  const dummy = useMemo(() => new Object3D(), []);

  useLayoutEffect(() => {
    const mesh = meshRef.current;
    if (!mesh) {
      return;
    }
    for (let i = 0; i < cubes.length; i++) {
      const c = cubes[i];
      dummy.position.set(c.x, FLOOR_TOP + c.height / 2, c.z);
      dummy.scale.set(1, c.height, 1);
      dummy.updateMatrix();
      mesh.setMatrixAt(i, dummy.matrix);
    }
    mesh.count = cubes.length;
    mesh.instanceMatrix.needsUpdate = true;
  }, [cubes, dummy]);

  if (cubes.length === 0) {
    return null;
  }

  return (
    <instancedMesh
      ref={meshRef}
      args={[undefined, undefined, MAX_CUBES_PER_ZONE]}
    >
      <boxGeometry args={[CUBE, 1, CUBE]} />
      <meshStandardMaterial color={color} roughness={0.5} metalness={0.05} />
    </instancedMesh>
  );
}

interface LotClusterProps {
  lots: GelLotDto[];
  centerX: number;
  centerZ: number;
}

/** Arrange a capped sample of a zone's lots in a small inner grid, drawn as instanced cubes per state. */
const LotCluster = memo(function LotCluster({
  lots,
  centerX,
  centerZ,
}: LotClusterProps) {
  const byState = useMemo(() => {
    const inner = ZONE_SIZE - PAD * 2;
    const perRow = Math.max(1, Math.floor(inner / (CUBE + 0.25)));
    const pitch = inner / perRow;
    const origin = -inner / 2 + pitch / 2;

    const groups: Record<LotState, CubePlacement[]> = {
      normal: [],
      atRisk: [],
      expired: [],
    };

    prioritizeAndCap(lots).forEach((lot, index) => {
      const col = index % perRow;
      const row = Math.floor(index / perRow);
      groups[lotState(lot)].push({
        x: origin + col * pitch,
        z: origin + (row % perRow) * pitch,
        height: stackHeight(lot.quantity),
      });
    });

    return groups;
  }, [lots]);

  return (
    <group position={[centerX, 0, centerZ]}>
      <InstancedCubes cubes={byState.normal} color={STATE_COLOR.normal} />
      <InstancedCubes cubes={byState.atRisk} color={STATE_COLOR.atRisk} />
      <InstancedCubes cubes={byState.expired} color={STATE_COLOR.expired} />
      {/* One count label per zone (not per lot) — cheap and readable. */}
      <Text
        position={[0, 3.6, 0]}
        rotation={[-Math.PI / 2, 0, 0]}
        fontSize={0.9}
        color="#f2f6fb"
        outlineWidth={0.04}
        outlineColor="#0b0f14"
        anchorX="center"
        anchorY="middle"
      >
        {String(lots.length)}
      </Text>
    </group>
  );
});

interface LotsProps {
  placements: ZonePlacement[];
  lotsByZone: Map<string | null, GelLotDto[]>;
}

export function Lots({ placements, lotsByZone }: LotsProps) {
  const placementById = useMemo(() => {
    const map = new Map<string, ZonePlacement>();
    for (const p of placements) {
      map.set(p.zone.id, p);
    }
    return map;
  }, [placements]);

  const unslottedZ = useMemo(() => {
    let maxZ = 0;
    for (const p of placements) {
      maxZ = Math.max(maxZ, p.centerZ);
    }
    return maxZ + ZONE_SIZE;
  }, [placements]);

  return (
    <group>
      {Array.from(lotsByZone.entries()).map(([zoneId, lots]) => {
        if (zoneId == null) {
          return (
            <LotCluster
              key="__unslotted__"
              lots={lots}
              centerX={0}
              centerZ={unslottedZ}
            />
          );
        }
        const placement = placementById.get(zoneId);
        if (!placement) {
          return null;
        }
        return (
          <LotCluster
            key={zoneId}
            lots={lots}
            centerX={placement.centerX}
            centerZ={placement.centerZ}
          />
        );
      })}
    </group>
  );
}