"use client";

/**
 * Breakroom: idle crew wait here until load/unload work is assigned.
 */

import { Text } from "@react-three/drei";

import { CELL_WORLD, FLOOR_TOP, IDLE_BAY, cellToWorld } from "@/lib/layout";

export function IdleBay() {
  const [x0, z0] = cellToWorld(IDLE_BAY.minX, IDLE_BAY.minY);
  const [x1, z1] = cellToWorld(IDLE_BAY.maxX, IDLE_BAY.maxY);
  const cx = (x0 + x1) / 2;
  const cz = (z0 + z1) / 2;
  const w = Math.abs(x1 - x0) + CELL_WORLD;
  const d = Math.abs(z1 - z0) + CELL_WORLD;

  return (
    <group position={[cx, 0, cz]}>
      <mesh position={[0, FLOOR_TOP * 0.55, 0]}>
        <boxGeometry args={[w, 0.12, d]} />
        <meshStandardMaterial color="#2a3544" roughness={0.7} metalness={0.15} />
      </mesh>
      <mesh position={[0, FLOOR_TOP * 0.7, 0]}>
        <boxGeometry args={[w - 0.35, 0.04, d - 0.35]} />
        <meshStandardMaterial
          color="#1e2833"
          emissive="#3d9dff"
          emissiveIntensity={0.15}
          roughness={0.6}
        />
      </mesh>
      {/* Low walls */}
      <mesh position={[0, FLOOR_TOP + 0.45, -d / 2]}>
        <boxGeometry args={[w, 0.9, 0.12]} />
        <meshStandardMaterial color="#3a4658" metalness={0.25} roughness={0.45} />
      </mesh>
      <mesh position={[-w / 2, FLOOR_TOP + 0.45, 0]}>
        <boxGeometry args={[0.12, 0.9, d]} />
        <meshStandardMaterial color="#3a4658" metalness={0.25} roughness={0.45} />
      </mesh>
      <mesh position={[w / 2, FLOOR_TOP + 0.45, 0]}>
        <boxGeometry args={[0.12, 0.9, d]} />
        <meshStandardMaterial color="#3a4658" metalness={0.25} roughness={0.45} />
      </mesh>
      <Text
        position={[0, FLOOR_TOP + 0.08, d / 2 - 0.4]}
        rotation={[-Math.PI / 2, 0, 0]}
        fontSize={0.4}
        color="#9eb6d4"
        anchorX="center"
        anchorY="middle"
      >
        BREAKROOM
      </Text>
    </group>
  );
}
