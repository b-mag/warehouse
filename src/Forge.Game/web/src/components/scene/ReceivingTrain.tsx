"use client";

/**
 * Inbound maglev: locomotive + floating flatbeds on an extended luminous guideway.
 * Driven by authoritative TrainDto phase from motionStore (Away / Approaching /
 * Unloading / Departing). Guideway stays; consist animates in/out.
 */

import { useFrame } from "@react-three/fiber";
import { useMemo, useRef } from "react";
import { InstancedMesh, Object3D, type Group } from "three";

import { FLOOR_TOP, RAIL_Y, cellToWorld } from "@/lib/layout";
import { getMotion } from "@/lib/motionStore";

const CUBE = 0.75;
const BOX_HEIGHT = 0.85;
const MAX_TRAIN_BOXES = 40;
const CAR_CAPACITY = 8;
const MAX_CARS = 5;
const RAIL_DECK_Y = FLOOR_TOP + 0.35;
const APPROACH_OFFSET = 48;

export function ReceivingTrain({ simSpeed }: { simSpeed: number }) {
  const consistRef = useRef<Group>(null);
  const meshRef = useRef<InstancedMesh>(null);
  const carRefs = useRef<(Group | null)[]>([]);
  const dummy = useMemo(() => new Object3D(), []);
  const progress = useRef(0);
  const lastPhase = useRef("Away");

  const railXStart = 1;
  const railLength = 42;
  const [railWorldX, railWorldZ] = cellToWorld(railXStart + 12, RAIL_Y);
  const [engineX] = cellToWorld(railXStart, RAIL_Y);
  const dockX = 0;

  useFrame((_, delta) => {
    const train = getMotion().train;
    const consist = consistRef.current;
    if (!consist) {
      return;
    }

    const phase = train?.phase ?? "Away";
    const carCount = Math.max(1, Math.min(MAX_CARS, train?.carCount ?? 1));
    const lotIds = train?.lotIds ?? getMotion().inboundQueueLotIds;
    const count =
      phase === "Unloading" || phase === "Approaching"
        ? Math.min(lotIds.length, MAX_TRAIN_BOXES)
        : 0;

    for (let c = 0; c < MAX_CARS; c++) {
      const car = carRefs.current[c];
      if (car) {
        car.visible = c < carCount && phase !== "Away";
      }
    }

    if (phase !== lastPhase.current) {
      if (phase === "Approaching") {
        progress.current = 0;
        consist.position.x = dockX + APPROACH_OFFSET;
        consist.visible = true;
      } else if (phase === "Departing") {
        progress.current = 0;
      } else if (phase === "Away") {
        consist.visible = false;
        progress.current = 0;
      } else if (phase === "Unloading") {
        consist.position.x = dockX;
        consist.visible = true;
      }
      lastPhase.current = phase;
    }

    // Wall-clock slide (~4s in / ~3s out). Do not multiply by simSpeed — server phase
    // durations already scale with sim time, and 60× made the consist teleport.
    void simSpeed;
    const approachRate = 1 / 4;
    const departRate = 1 / 3;

    if (phase === "Approaching") {
      progress.current = Math.min(1, progress.current + delta * approachRate);
      const t = progress.current;
      const ease = 1 - (1 - t) * (1 - t);
      consist.position.x = dockX + APPROACH_OFFSET * (1 - ease);
      consist.visible = true;
    } else if (phase === "Unloading") {
      consist.position.x = dockX;
      consist.visible = true;
    } else if (phase === "Departing") {
      progress.current = Math.min(1, progress.current + delta * departRate);
      const t = progress.current;
      const ease = t * t;
      consist.position.x = dockX - APPROACH_OFFSET * ease;
      consist.visible = t < 0.98;
    } else {
      consist.visible = false;
    }

    const mesh = meshRef.current;
    if (mesh) {
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
      mesh.visible = count > 0 && consist.visible;
    }
  });

  return (
    <group>
      <mesh position={[railWorldX, RAIL_DECK_Y, railWorldZ]}>
        <boxGeometry args={[railLength, 0.18, 2.8]} />
        <meshStandardMaterial color="#151c26" roughness={0.75} metalness={0.2} />
      </mesh>
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
      <mesh position={[railWorldX, RAIL_DECK_Y + 0.08, railWorldZ]}>
        <boxGeometry args={[railLength, 0.04, 0.35]} />
        <meshStandardMaterial color="#3a9dff" emissive="#1a5080" emissiveIntensity={0.4} />
      </mesh>

      <group ref={consistRef} visible={false}>
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
          <mesh position={[-0.6, -0.55, 0]}>
            <boxGeometry args={[1.6, 0.08, 1.0]} />
            <meshStandardMaterial color="#5ec8ff" emissive="#3a9dff" emissiveIntensity={0.9} />
          </mesh>
          <mesh position={[0.9, -0.55, 0]}>
            <boxGeometry args={[1.2, 0.08, 1.0]} />
            <meshStandardMaterial color="#5ec8ff" emissive="#3a9dff" emissiveIntensity={0.9} />
          </mesh>
        </group>

        {Array.from({ length: MAX_CARS }, (_, car) => {
          const x = engineX + 3.6 + car * 4.4;
          return (
            <group
              key={car}
              ref={(g) => {
                carRefs.current[car] = g;
              }}
              position={[x, RAIL_DECK_Y + 0.65, railWorldZ]}
              visible={false}
            >
              <mesh>
                <boxGeometry args={[3.8, 0.28, 1.45]} />
                <meshStandardMaterial color="#6a7a90" roughness={0.35} metalness={0.55} />
              </mesh>
              <mesh position={[0, -0.42, 0]}>
                <boxGeometry args={[3.2, 0.06, 0.9]} />
                <meshStandardMaterial
                  color="#5ec8ff"
                  emissive="#2a8fd4"
                  emissiveIntensity={0.75}
                />
              </mesh>
            </group>
          );
        })}

        <instancedMesh
          ref={meshRef}
          args={[undefined, undefined, MAX_TRAIN_BOXES]}
          frustumCulled={false}
          visible={false}
        >
          <boxGeometry args={[CUBE, BOX_HEIGHT, CUBE]} />
          <meshStandardMaterial color="#7ad7ff" roughness={0.45} metalness={0.15} />
        </instancedMesh>
      </group>
    </group>
  );
}
