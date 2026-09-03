"use client";

/**
 * Inbound train: locomotive + flatbed cars on a dedicated rail (separate from ship docks).
 * Cyan cubes = pallets waiting for put-away (capped at one train-car visual capacity).
 */

import { useLayoutEffect, useMemo, useRef } from "react";
import { InstancedMesh, Object3D } from "three";

const CELL_WORLD = 1.1;
const FLOOR_TOP = 0.4;

const GRID_WIDTH_CELLS = 32;
const GRID_HEIGHT_CELLS = 32;
const GRID_CENTER_X = (GRID_WIDTH_CELLS - 1) / 2;
const GRID_CENTER_Y = (GRID_HEIGHT_CELLS - 1) / 2;

function cellToWorld(x: number, y: number): [number, number] {
  return [(x - GRID_CENTER_X) * CELL_WORLD, (y - GRID_CENTER_Y) * CELL_WORLD];
}

const CUBE = 0.75;
const BOX_HEIGHT = 0.85;
/** Matches VisualSimulationConstants.TrainCarCapacityPallets */
const MAX_TRAIN_BOXES = 40;
const CAR_CAPACITY = 8;
const MAX_CARS = 5;

export function ReceivingTrain({ lotIds }: { lotIds: string[] }) {
  const meshRef = useRef<InstancedMesh>(null);
  const dummy = useMemo(() => new Object3D(), []);

  const count = Math.min(lotIds.length, MAX_TRAIN_BOXES);
  const carCount = Math.max(1, Math.min(MAX_CARS, Math.ceil(Math.max(count, 1) / CAR_CAPACITY)));

  // Receiving rail: low-Y edge, left side — away from high-Y ship berths.
  const railY = 0;
  const railXStart = 2;

  useLayoutEffect(() => {
    const mesh = meshRef.current;
    if (!mesh) {
      return;
    }

    const [rx, rz] = cellToWorld(railXStart, railY);

    for (let i = 0; i < count; i++) {
      const car = Math.floor(i / CAR_CAPACITY);
      const seat = i % CAR_CAPACITY;
      const carBaseX = rx + 3.2 + car * 4.2;
      const x = carBaseX + (seat - (CAR_CAPACITY - 1) / 2) * (CUBE * 0.95);
      const z = rz;
      dummy.position.set(x, FLOOR_TOP + BOX_HEIGHT / 2 + 0.35, z);
      dummy.scale.set(1, 1, 1);
      dummy.updateMatrix();
      mesh.setMatrixAt(i, dummy.matrix);
    }

    mesh.count = count;
    mesh.instanceMatrix.needsUpdate = true;
  }, [count, dummy]);

  const [railWorldX, railWorldZ] = cellToWorld(railXStart + 10, railY);
  const railLength = 28;
  const [engineX] = cellToWorld(railXStart, railY);

  return (
    <group>
      {/* Rail bed */}
      <mesh position={[railWorldX, FLOOR_TOP * 0.35, railWorldZ]} receiveShadow>
        <boxGeometry args={[railLength, 0.18, 2.4]} />
        <meshStandardMaterial color="#5a4632" roughness={0.85} metalness={0.05} />
      </mesh>
      <mesh position={[railWorldX, FLOOR_TOP * 0.55, railWorldZ - 0.75]}>
        <boxGeometry args={[railLength, 0.08, 0.14]} />
        <meshStandardMaterial color="#8a8f98" metalness={0.6} roughness={0.35} />
      </mesh>
      <mesh position={[railWorldX, FLOOR_TOP * 0.55, railWorldZ + 0.75]}>
        <boxGeometry args={[railLength, 0.08, 0.14]} />
        <meshStandardMaterial color="#8a8f98" metalness={0.6} roughness={0.35} />
      </mesh>

      {/* Locomotive — distinct from worker cones. */}
      <group position={[engineX, FLOOR_TOP + 0.55, railWorldZ]}>
        <mesh position={[0, 0.15, 0]}>
          <boxGeometry args={[2.4, 1.1, 1.6]} />
          <meshStandardMaterial color="#c45c26" roughness={0.45} metalness={0.2} />
        </mesh>
        <mesh position={[-0.55, 0.85, 0]}>
          <boxGeometry args={[1.1, 0.9, 1.45]} />
          <meshStandardMaterial color="#2f353e" roughness={0.5} metalness={0.15} />
        </mesh>
        <mesh position={[1.0, 0.55, 0]} rotation={[0, 0, Math.PI / 2]}>
          <cylinderGeometry args={[0.35, 0.45, 0.9, 8]} />
          <meshStandardMaterial color="#d9783a" roughness={0.4} metalness={0.25} />
        </mesh>
      </group>

      {/* Flatbed cars */}
      {Array.from({ length: carCount }, (_, car) => {
        const x = engineX + 3.2 + car * 4.2;
        return (
          <group key={car} position={[x, FLOOR_TOP + 0.35, railWorldZ]}>
            <mesh>
              <boxGeometry args={[3.6, 0.35, 1.5]} />
              <meshStandardMaterial color="#6b7280" roughness={0.55} metalness={0.2} />
            </mesh>
            <mesh position={[-1.5, -0.15, 0.55]}>
              <cylinderGeometry args={[0.22, 0.22, 0.2, 10]} />
              <meshStandardMaterial color="#22262e" />
            </mesh>
            <mesh position={[1.5, -0.15, 0.55]}>
              <cylinderGeometry args={[0.22, 0.22, 0.2, 10]} />
              <meshStandardMaterial color="#22262e" />
            </mesh>
            <mesh position={[-1.5, -0.15, -0.55]}>
              <cylinderGeometry args={[0.22, 0.22, 0.2, 10]} />
              <meshStandardMaterial color="#22262e" />
            </mesh>
            <mesh position={[1.5, -0.15, -0.55]}>
              <cylinderGeometry args={[0.22, 0.22, 0.2, 10]} />
              <meshStandardMaterial color="#22262e" />
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
          <meshStandardMaterial color="#7ad7ff" roughness={0.5} metalness={0.05} />
        </instancedMesh>
      ) : null}
    </group>
  );
}
