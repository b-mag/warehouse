"use client";

/**
 * Inbound maglev: locomotive + floating flatbeds on an extended luminous guideway.
 * The guideway sits clearly above the warehouse floor slab.
 * Cyan cubes = pallets waiting for put-away.
 */

import { useLayoutEffect, useMemo, useRef } from "react";
import { InstancedMesh, Object3D } from "three";

import { FLOOR_TOP, RAIL_Y, cellToWorld } from "@/lib/layout";

const CUBE = 0.75;
const BOX_HEIGHT = 0.85;
const MAX_TRAIN_BOXES = 40;
const CAR_CAPACITY = 8;
const MAX_CARS = 5;

/** Top of warehouse slab (~FLOOR_TOP); rail deck sits above this. */
const RAIL_DECK_Y = FLOOR_TOP + 0.35;

export function ReceivingTrain({ lotIds }: { lotIds: string[] }) {
  const meshRef = useRef<InstancedMesh>(null);
  const dummy = useMemo(() => new Object3D(), []);

  const count = Math.min(lotIds.length, MAX_TRAIN_BOXES);
  const carCount = Math.max(1, Math.min(MAX_CARS, Math.ceil(Math.max(count, 1) / CAR_CAPACITY)));

  const railXStart = 1;
  const railLength = 42;

  useLayoutEffect(() => {
    const mesh = meshRef.current;
    if (!mesh) {
      return;
    }

    const [rx, rz] = cellToWorld(railXStart, RAIL_Y);

    for (let i = 0; i < count; i++) {
      const car = Math.floor(i / CAR_CAPACITY);
      const seat = i % CAR_CAPACITY;
      const carBaseX = rx + 3.6 + car * 4.4;
      const x = carBaseX + (seat - (CAR_CAPACITY - 1) / 2) * (CUBE * 0.95);
      dummy.position.set(x, RAIL_DECK_Y + BOX_HEIGHT / 2 + 0.55, rz);
      dummy.scale.set(1, 1, 1);
      dummy.updateMatrix();
      mesh.setMatrixAt(i, dummy.matrix);
    }

    mesh.count = count;
    mesh.instanceMatrix.needsUpdate = true;
  }, [count, dummy]);

  const [railWorldX, railWorldZ] = cellToWorld(railXStart + 12, RAIL_Y);
  const [engineX] = cellToWorld(railXStart, RAIL_Y);

  return (
    <group>
      {/* Maglev guideway bed — above warehouse floor */}
      <mesh position={[railWorldX, RAIL_DECK_Y, railWorldZ]}>
        <boxGeometry args={[railLength, 0.18, 2.8]} />
        <meshStandardMaterial color="#151c26" roughness={0.75} metalness={0.2} />
      </mesh>
      {/* Twin luminous rails */}
      <mesh position={[railWorldX, RAIL_DECK_Y + 0.12, railWorldZ - 0.7]}>
        <boxGeometry args={[railLength, 0.1, 0.18]} />
        <meshStandardMaterial
          color="#5ec8ff"
          emissive="#2a8fd4"
          emissiveIntensity={0.7}
          metalness={0.5}
          roughness={0.25}
        />
      </mesh>
      <mesh position={[railWorldX, RAIL_DECK_Y + 0.12, railWorldZ + 0.7]}>
        <boxGeometry args={[railLength, 0.1, 0.18]} />
        <meshStandardMaterial
          color="#5ec8ff"
          emissive="#2a8fd4"
          emissiveIntensity={0.7}
          metalness={0.5}
          roughness={0.25}
        />
      </mesh>
      {/* Center guide strip */}
      <mesh position={[railWorldX, RAIL_DECK_Y + 0.08, railWorldZ]}>
        <boxGeometry args={[railLength, 0.04, 0.35]} />
        <meshStandardMaterial color="#3a9dff" emissive="#1a5080" emissiveIntensity={0.4} />
      </mesh>

      {/* Maglev locomotive */}
      <group position={[engineX, RAIL_DECK_Y + 0.85, railWorldZ]}>
        <mesh position={[0.2, 0.15, 0]}>
          <boxGeometry args={[2.8, 0.85, 1.35]} />
          <meshStandardMaterial color="#9aafc6" roughness={0.25} metalness={0.7} />
        </mesh>
        <mesh position={[-0.7, 0.55, 0]}>
          <boxGeometry args={[1.2, 0.7, 1.2]} />
          <meshStandardMaterial
            color="#7ec8ff"
            emissive="#2a6fa0"
            emissiveIntensity={0.35}
            roughness={0.2}
            metalness={0.45}
            transparent
            opacity={0.8}
          />
        </mesh>
        <mesh position={[1.35, 0.1, 0]} rotation={[0, 0, Math.PI / 2]}>
          <coneGeometry args={[0.45, 1.1, 6]} />
          <meshStandardMaterial color="#c5d2e4" roughness={0.2} metalness={0.75} />
        </mesh>
        {/* Hover glow pads */}
        <mesh position={[-0.6, -0.55, 0]}>
          <boxGeometry args={[1.6, 0.08, 1.0]} />
          <meshStandardMaterial color="#5ec8ff" emissive="#3a9dff" emissiveIntensity={0.9} />
        </mesh>
        <mesh position={[0.9, -0.55, 0]}>
          <boxGeometry args={[1.2, 0.08, 1.0]} />
          <meshStandardMaterial color="#5ec8ff" emissive="#3a9dff" emissiveIntensity={0.9} />
        </mesh>
      </group>

      {/* Floating flatbeds */}
      {Array.from({ length: carCount }, (_, car) => {
        const x = engineX + 3.6 + car * 4.4;
        return (
          <group key={car} position={[x, RAIL_DECK_Y + 0.65, railWorldZ]}>
            <mesh>
              <boxGeometry args={[3.8, 0.28, 1.45]} />
              <meshStandardMaterial color="#6a7a90" roughness={0.35} metalness={0.55} />
            </mesh>
            <mesh position={[0, -0.42, 0]}>
              <boxGeometry args={[3.2, 0.06, 0.9]} />
              <meshStandardMaterial color="#5ec8ff" emissive="#2a8fd4" emissiveIntensity={0.75} />
            </mesh>
            <mesh position={[-1.4, 0.2, 0]}>
              <boxGeometry args={[0.12, 0.35, 1.35]} />
              <meshStandardMaterial color="#8fa0b8" metalness={0.6} roughness={0.3} />
            </mesh>
            <mesh position={[1.4, 0.2, 0]}>
              <boxGeometry args={[0.12, 0.35, 1.35]} />
              <meshStandardMaterial color="#8fa0b8" metalness={0.6} roughness={0.3} />
            </mesh>
          </group>
        );
      })}

      {count > 0 ? (
        <instancedMesh
          ref={meshRef}
          args={[undefined, undefined, MAX_TRAIN_BOXES]}
          frustumCulled={false}
        >
          <boxGeometry args={[CUBE, BOX_HEIGHT, CUBE]} />
          <meshStandardMaterial color="#7ad7ff" roughness={0.45} metalness={0.15} />
        </instancedMesh>
      ) : null}
    </group>
  );
}
