"use client";

/**
 * Warehouse slab under zones, idle bay, rail, and ship pads.
 */

import { warehouseFloorBounds, type ZonePlacement, FLOOR_TOP } from "@/lib/layout";

interface WarehouseFloorProps {
  placements: ZonePlacement[];
  openDockBays: number;
}

export function WarehouseFloor({ placements, openDockBays }: WarehouseFloorProps) {
  const b = warehouseFloorBounds(placements, openDockBays);
  return (
    <group>
      {/* Concrete slab — readable mid-gray so zones/agents pop (not black void). */}
      <mesh position={[b.centerX, FLOOR_TOP * 0.45, b.centerZ]} receiveShadow>
        <boxGeometry args={[b.width, FLOOR_TOP * 0.9, b.depth]} />
        <meshStandardMaterial color="#8b929c" roughness={0.88} metalness={0.04} />
      </mesh>
      {/* Slightly lighter poured-panel surface */}
      <mesh position={[b.centerX, FLOOR_TOP * 0.92, b.centerZ]}>
        <boxGeometry args={[b.width - 0.4, 0.02, b.depth - 0.4]} />
        <meshStandardMaterial
          color="#9aa3ae"
          roughness={0.82}
          metalness={0.06}
          transparent
          opacity={0.95}
        />
      </mesh>
      {/* Edge trim bordering rail + ship sides */}
      <mesh position={[b.centerX, FLOOR_TOP * 0.95, b.centerZ - b.depth / 2 + 0.12]}>
        <boxGeometry args={[b.width, 0.08, 0.22]} />
        <meshStandardMaterial color="#3d9dff" emissive="#1a4a7a" emissiveIntensity={0.35} />
      </mesh>
      <mesh position={[b.centerX, FLOOR_TOP * 0.95, b.centerZ + b.depth / 2 - 0.12]}>
        <boxGeometry args={[b.width, 0.08, 0.22]} />
        <meshStandardMaterial color="#3d9dff" emissive="#1a4a7a" emissiveIntensity={0.35} />
      </mesh>
    </group>
  );
}
