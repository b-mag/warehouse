"use client";

/**
 * Gel lots as extruded cubes grouped into their zone footprint (task 37.2; Req 24.2).
 *
 * Color reflects the authoritative lot state: normal (pale), at-risk (amber), expired
 * (red). A zone can hold hundreds of lots, so we render a READABLE, capped sample of
 * cubes per zone (prioritizing at-risk/expired so problems stay visible) plus a small
 * count label for the zone. We deliberately do NOT render a text label per lot: a
 * thousand SDF text meshes exhausts the GPU and crashes the WebGL context. No expiry/
 * at-risk state is computed here — the flags come straight from the DTO.
 */

import { Text } from "@react-three/drei";
import { useMemo } from "react";

import type { GelLotDto } from "@/lib/contracts";
import {
  ZONE_SIZE,
  lotColor,
  type ZonePlacement,
} from "@/lib/layout";

const CUBE = 0.9;
const FLOOR_TOP = 0.4;
const PAD = 1.0;

/** Max cubes drawn per zone. Beyond this, the zone count label conveys the rest. */
const MAX_CUBES_PER_ZONE = 48;

/** Height multiplier from quantity (clamped so the view stays readable). */
function stackHeight(quantity: number): number {
  const h = 0.6 + Math.log10(Math.max(1, quantity)) * 0.8;
  return Math.min(h, 3.2);
}

/** Order lots so problem lots (expired, then at-risk) are always among those drawn. */
function prioritize(lots: GelLotDto[]): GelLotDto[] {
  return [...lots].sort((a, b) => {
    const rank = (l: GelLotDto) => (l.isExpired ? 0 : l.atRisk ? 1 : 2);
    return rank(a) - rank(b);
  });
}

interface LotClusterProps {
  lots: GelLotDto[];
  centerX: number;
  centerZ: number;
}

/** Arrange a capped sample of a zone's lots in a small inner grid on top of the floor. */
function LotCluster({ lots, centerX, centerZ }: LotClusterProps) {
  const inner = ZONE_SIZE - PAD * 2;
  const perRow = Math.max(1, Math.floor(inner / (CUBE + 0.25)));
  const pitch = inner / perRow;
  const origin = -inner / 2 + pitch / 2;

  const ordered = useMemo(() => prioritize(lots), [lots]);
  const shown = ordered.slice(0, MAX_CUBES_PER_ZONE);
  const overflow = lots.length - shown.length;

  return (
    <group position={[centerX, 0, centerZ]}>
      {shown.map((lot, index) => {
        const col = index % perRow;
        const row = Math.floor(index / perRow);
        const x = origin + col * pitch;
        const z = origin + (row % perRow) * pitch;
        const height = stackHeight(lot.quantity);
        return (
          <mesh
            key={lot.id}
            position={[x, FLOOR_TOP + height / 2, z]}
            castShadow
          >
            <boxGeometry args={[CUBE, height, CUBE]} />
            <meshStandardMaterial
              color={lotColor(lot)}
              emissive={lot.isExpired ? "#5c0d0d" : "#000000"}
              emissiveIntensity={lot.isExpired ? 0.5 : 0}
              roughness={0.5}
              metalness={0.05}
            />
          </mesh>
        );
      })}
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
        {overflow > 0 ? `${lots.length} lots` : `${lots.length}`}
      </Text>
    </group>
  );
}

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