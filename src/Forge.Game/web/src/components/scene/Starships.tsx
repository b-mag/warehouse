"use client";

/**
 * Starship berths + wedge hulls.
 * Dock pads are fixed terminal cubes; ships lerp in/out based on authoritative phase.
 */

import { Text } from "@react-three/drei";
import { useFrame } from "@react-three/fiber";
import { useMemo, useRef } from "react";
import type { Group } from "three";

import type { StarshipDto } from "@/lib/contracts";

interface StarshipsProps {
  starships: StarshipDto[];
  /** Docking edge z-coordinate (ship side of the warehouse). */
  edgeZ: number;
  openDockBays: number;
  simSpeed: number;
}

const SHIP_W = 4;
const SHIP_H = 2.2;
const SHIP_D = 3;
const SPACING = 6;
const PAD_W = 5;
const PAD_H = 0.35;
const PAD_D = 4;
const LERP = 2.4;

function berthOriginX(bayCount: number): number {
  return (-(Math.max(1, bayCount) - 1) * SPACING) / 2;
}

function targetPose(
  phase: string,
  dockIndex: number,
  fallbackIndex: number,
  edgeZ: number,
  bayCount: number,
): { x: number; y: number; z: number; visible: boolean } {
  const originX = berthOriginX(bayCount);
  const berth = dockIndex >= 0 ? dockIndex : fallbackIndex;
  const x = originX + berth * SPACING;
  const dockZ = edgeZ + SHIP_D;
  const dockY = SHIP_H / 2 + 0.55;

  switch (phase) {
    case "Approaching":
      return { x, y: dockY + 8, z: dockZ + 22, visible: true };
    case "Departing":
      return { x, y: dockY + 10, z: dockZ + 28, visible: true };
    case "Away":
      return { x, y: dockY + 18, z: dockZ + 45, visible: false };
    case "Unloading":
    case "Loading":
    case "Docked":
    default:
      return { x, y: dockY, z: dockZ, visible: true };
  }
}

function DockPads({ edgeZ, openDockBays }: { edgeZ: number; openDockBays: number }) {
  const bays = Math.max(0, openDockBays);
  const originX = berthOriginX(bays);
  return (
    <group>
      {Array.from({ length: bays }, (_, i) => {
        const x = originX + i * SPACING;
        return (
          <mesh key={i} position={[x, PAD_H / 2 + 0.05, edgeZ + PAD_D / 2]} castShadow>
            <boxGeometry args={[PAD_W, PAD_H, PAD_D]} />
            <meshStandardMaterial color="#3d6b8c" roughness={0.55} metalness={0.15} />
          </mesh>
        );
      })}
    </group>
  );
}

function StarshipHull({
  ship,
  index,
  edgeZ,
  bayCount,
  simSpeed,
}: {
  ship: StarshipDto;
  index: number;
  edgeZ: number;
  bayCount: number;
  simSpeed: number;
}) {
  const ref = useRef<Group>(null);
  const target = targetPose(ship.phase, ship.dockIndex, index, edgeZ, bayCount);
  const fill =
    ship.capacity > 0 ? Math.max(0, Math.min(1, ship.loaded / ship.capacity)) : 0;

  useFrame((_, delta) => {
    const g = ref.current;
    if (!g) return;
    const speed = LERP * Math.max(0.25, simSpeed) * delta;
    g.position.x += (target.x - g.position.x) * Math.min(1, speed);
    g.position.y += (target.y - g.position.y) * Math.min(1, speed);
    g.position.z += (target.z - g.position.z) * Math.min(1, speed);
    g.visible = target.visible || g.position.y < target.y + 12;
  });

  const inFlight = ship.phase === "Approaching" || ship.phase === "Departing";
  const hull = "#6e7a8f";
  const accent = "#c9d2e0";

  return (
    <group ref={ref} position={[target.x, target.y, target.z]}>
      {/* Main fuselage — elongated along dock axis so it reads as a craft, not a hammer. */}
      <mesh castShadow position={[0, 0.1, 0]}>
        <boxGeometry args={[SHIP_W * 0.72, SHIP_H * 0.7, SHIP_D * 1.55]} />
        <meshStandardMaterial color={hull} roughness={0.4} metalness={0.35} />
      </mesh>
      {/* Nose / cockpit toward warehouse */}
      <mesh castShadow position={[0, 0.15, -SHIP_D * 0.85]} rotation={[Math.PI / 2, 0, 0]}>
        <coneGeometry args={[SHIP_W * 0.28, SHIP_D * 0.55, 6]} />
        <meshStandardMaterial color={accent} roughness={0.35} metalness={0.4} />
      </mesh>
      <mesh position={[0, SHIP_H * 0.22, -SHIP_D * 0.55]}>
        <boxGeometry args={[SHIP_W * 0.35, SHIP_H * 0.22, SHIP_D * 0.35]} />
        <meshStandardMaterial color="#9eb0c8" roughness={0.3} metalness={0.45} />
      </mesh>
      {/* Side cargo wings */}
      <mesh castShadow position={[0, -0.15, SHIP_D * 0.15]}>
        <boxGeometry args={[SHIP_W * 1.35, SHIP_H * 0.35, SHIP_D * 0.85]} />
        <meshStandardMaterial color="#5a6578" roughness={0.5} metalness={0.3} />
      </mesh>
      {/* Twin rear thrusters */}
      <mesh position={[-SHIP_W * 0.28, 0, SHIP_D * 0.85]} rotation={[Math.PI / 2, 0, 0]}>
        <cylinderGeometry args={[0.28, 0.38, 0.55, 8]} />
        <meshStandardMaterial color="#3a4250" metalness={0.5} roughness={0.35} />
      </mesh>
      <mesh position={[SHIP_W * 0.28, 0, SHIP_D * 0.85]} rotation={[Math.PI / 2, 0, 0]}>
        <cylinderGeometry args={[0.28, 0.38, 0.55, 8]} />
        <meshStandardMaterial color="#3a4250" metalness={0.5} roughness={0.35} />
      </mesh>
      {inFlight ? (
        <>
          <mesh position={[-SHIP_W * 0.28, 0, SHIP_D * 1.15]}>
            <sphereGeometry args={[0.22, 8, 8]} />
            <meshStandardMaterial color="#7ec8ff" emissive="#3a9fff" emissiveIntensity={1.2} />
          </mesh>
          <mesh position={[SHIP_W * 0.28, 0, SHIP_D * 1.15]}>
            <sphereGeometry args={[0.22, 8, 8]} />
            <meshStandardMaterial color="#7ec8ff" emissive="#3a9fff" emissiveIntensity={1.2} />
          </mesh>
        </>
      ) : null}

      {/* Cargo fill bar */}
      <group position={[-SHIP_W / 2 + 0.15, SHIP_H / 2 + 0.05, 0]}>
        <mesh position={[SHIP_W / 2 - 0.15, 0, 0]}>
          <boxGeometry args={[SHIP_W - 0.3, 0.16, 0.35]} />
          <meshStandardMaterial color="#1a1f28" />
        </mesh>
        <mesh position={[((SHIP_W - 0.3) * fill) / 2, 0, 0.01]}>
          <boxGeometry args={[(SHIP_W - 0.3) * fill || 0.001, 0.22, 0.42]} />
          <meshStandardMaterial color="#4edc7a" emissive="#0f5c2c" emissiveIntensity={0.4} />
        </mesh>
      </group>
      <Text
        position={[0, SHIP_H / 2 + 0.65, 0]}
        rotation={[-Math.PI / 2, 0, 0]}
        fontSize={0.5}
        color="#f2f6fb"
        anchorX="center"
        anchorY="middle"
      >
        {`${ship.loaded}/${ship.capacity} · SHIP · ${ship.phase}`}
      </Text>
    </group>
  );
}

export function Starships({ starships, edgeZ, openDockBays, simSpeed }: StarshipsProps) {
  const ordered = useMemo(
    () => [...starships].sort((a, b) => a.id.localeCompare(b.id)),
    [starships],
  );
  const bayCount = Math.max(openDockBays, 1);

  return (
    <group>
      <DockPads edgeZ={edgeZ} openDockBays={openDockBays} />
      {ordered.map((ship, index) => (
        <StarshipHull
          key={ship.id}
          ship={ship}
          index={index}
          edgeZ={edgeZ}
          bayCount={bayCount}
          simSpeed={simSpeed}
        />
      ))}
    </group>
  );
}
