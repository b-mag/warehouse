"use client";

/**
 * Temperature zones as extruded colored floor regions (task 37.2; Req 24.2).
 *
 * Each zone is a shallow box (pseudo-3D depth) tinted by its temperature band. A zone
 * holding at-risk or expired lots gets a distinct emissive floor + border ring so
 * cold-chain trouble is glanceable. Lot cubes are rendered by the parent grouped into
 * the zone footprint.
 */

import { Text } from "@react-three/drei";

import type { GelLotDto } from "@/lib/contracts";
import {
  BAND_COLOR,
  ZONE_SIZE,
  temperatureBand,
  type ZonePlacement,
} from "@/lib/layout";

interface ZonesProps {
  placements: ZonePlacement[];
  lotsByZone: Map<string | null, GelLotDto[]>;
}

const FLOOR_HEIGHT = 0.4;
const BORDER = 0.35;

export function Zones({ placements, lotsByZone }: ZonesProps) {
  return (
    <group>
      {placements.map((placement) => {
        const { zone, centerX, centerZ } = placement;
        const band = temperatureBand(zone);
        const lots = lotsByZone.get(zone.id) ?? [];
        const hasTrouble = lots.some((lot) => lot.isExpired || lot.atRisk);
        return (
          <group key={zone.id} position={[centerX, 0, centerZ]}>
            {/* Distinct trouble ring: a slightly larger emissive slab beneath the floor. */}
            {hasTrouble && (
              <mesh position={[0, FLOOR_HEIGHT / 2 - 0.05, 0]}>
                <boxGeometry
                  args={[ZONE_SIZE + BORDER, FLOOR_HEIGHT, ZONE_SIZE + BORDER]}
                />
                <meshStandardMaterial
                  color="#ff8a5c"
                  emissive="#ff5a3c"
                  emissiveIntensity={0.9}
                  roughness={0.6}
                />
              </mesh>
            )}
            {/* Extruded floor region. */}
            <mesh position={[0, FLOOR_HEIGHT / 2, 0]} receiveShadow>
              <boxGeometry args={[ZONE_SIZE, FLOOR_HEIGHT, ZONE_SIZE]} />
              <meshStandardMaterial
                color={BAND_COLOR[band]}
                emissive={hasTrouble ? "#7a1f10" : "#000000"}
                emissiveIntensity={hasTrouble ? 0.4 : 0}
                roughness={0.85}
              />
            </mesh>
            {/* Capacity / band label, oriented flat for the top-down camera. */}
            <Text
              position={[0, FLOOR_HEIGHT + 0.05, ZONE_SIZE / 2 - 0.9]}
              rotation={[-Math.PI / 2, 0, 0]}
              fontSize={0.7}
              color="#f2f6fb"
              anchorX="center"
              anchorY="middle"
            >
              {`${band} · ${zone.stored}/${zone.capacity}`}
            </Text>
          </group>
        );
      })}
    </group>
  );
}
