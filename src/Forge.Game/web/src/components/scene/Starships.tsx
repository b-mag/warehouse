"use client";

/**
 * Starship berths + thick flat triangular ("Dorito") freighters.
 * Fly-in / hover-rotate-lower / mixed VTOL takeoff are client-only choreography.
 */

import { Text } from "@react-three/drei";
import { useFrame } from "@react-three/fiber";
import { useEffect, useMemo, useRef, useState } from "react";
import {
  ExtrudeGeometry,
  Shape,
  type Group,
} from "three";

import type { StarshipDto } from "@/lib/contracts";

interface StarshipsProps {
  starships: StarshipDto[];
  edgeZ: number;
  openDockBays: number;
  simSpeed: number;
}

/** Triangle size (point-to-base) and slab thickness (50% of prior Dorito slab). */
const SHIP_SIZE = 4.6;
const SHIP_THICK = 0.475;
const SPACING = 7;
const PAD_W = 5.6;
const PAD_H = 0.28;
const PAD_D = 5.0;
/** Hover height above pad when settled. */
const HOVER_Y = 1.2;

type FlightMode =
  | "away"
  | "arriving_hover"
  | "arriving_rotate"
  | "arriving_lower"
  | "docked"
  | "depart_rise"
  | "depart_fly";

type TakeoffStyle = "vtol" | "climb_fly";

function berthOriginX(bayCount: number): number {
  return (-(Math.max(1, bayCount) - 1) * SPACING) / 2;
}

function dockPose(
  dockIndex: number,
  fallbackIndex: number,
  edgeZ: number,
  bayCount: number,
): { x: number; y: number; z: number } {
  const originX = berthOriginX(bayCount);
  const berth = dockIndex >= 0 ? dockIndex : fallbackIndex;
  return {
    x: originX + berth * SPACING,
    y: HOVER_Y,
    z: edgeZ + PAD_D * 0.55,
  };
}

/** Deterministic takeoff mix from ship id (no RNG). */
function takeoffStyle(shipId: string): TakeoffStyle {
  let h = 0;
  for (let i = 0; i < shipId.length; i++) {
    h = (Math.imul(h, 31) + shipId.charCodeAt(i)) | 0;
  }
  return (h & 1) === 0 ? "vtol" : "climb_fly";
}

/**
 * Geometry: shape +Y becomes world −Z after rotateX(-π/2).
 * Docked: point away from warehouse (+Z) ⇒ yaw = π.
 * Approach: fly in from +Z traveling −Z with point forward (−Z) ⇒ yaw = 0.
 */
const DOCK_YAW = Math.PI;
const APPROACH_YAW = 0;

function makeDoritoGeometry(): ExtrudeGeometry {
  const s = SHIP_SIZE;
  const shape = new Shape();
  // Point at +Y in shape space → after rotation becomes +Z (away from warehouse).
  shape.moveTo(0, s * 0.58);
  shape.lineTo(s * 0.52, -s * 0.42);
  shape.lineTo(-s * 0.52, -s * 0.42);
  shape.closePath();

  const geom = new ExtrudeGeometry(shape, {
    depth: SHIP_THICK,
    bevelEnabled: true,
    bevelThickness: 0.08,
    bevelSize: 0.1,
    bevelSegments: 2,
  });
  // Center the slab on its thickness, then we'll orient in the hull group.
  geom.translate(0, 0, -SHIP_THICK / 2);
  geom.rotateX(-Math.PI / 2); // shape XY → XZ floor plane
  return geom;
}

function DockPads({ edgeZ, openDockBays }: { edgeZ: number; openDockBays: number }) {
  const bays = Math.max(0, openDockBays);
  const originX = berthOriginX(bays);
  return (
    <group>
      {Array.from({ length: bays }, (_, i) => {
        const x = originX + i * SPACING;
        return (
          <group key={i} position={[x, 0, edgeZ + PAD_D / 2]}>
            <mesh position={[0, PAD_H / 2 + 0.05, 0]}>
              <boxGeometry args={[PAD_W, PAD_H, PAD_D]} />
              <meshStandardMaterial color="#2a5f7a" roughness={0.4} metalness={0.35} />
            </mesh>
            <mesh position={[0, PAD_H + 0.08, 0]} rotation={[-Math.PI / 2, 0, 0]}>
              <ringGeometry args={[1.2, 1.7, 3]} />
              <meshStandardMaterial
                color="#5ec8ff"
                emissive="#2a8fd4"
                emissiveIntensity={0.55}
                transparent
                opacity={0.85}
              />
            </mesh>
          </group>
        );
      })}
    </group>
  );
}

function DoritoMesh({ thrustersOn }: { thrustersOn: boolean }) {
  const geometry = useMemo(() => makeDoritoGeometry(), []);
  const glow = thrustersOn ? 1.35 : 0.55;

  return (
    <group>
      {/* Main charcoal slab */}
      <mesh geometry={geometry} castShadow>
        <meshStandardMaterial color="#1a1d22" roughness={0.55} metalness={0.35} />
      </mesh>

      {/* Medium black dome on top */}
      <mesh position={[0, SHIP_THICK / 2 + 0.12, -SHIP_SIZE * 0.05]}>
        <sphereGeometry args={[0.72, 20, 12, 0, Math.PI * 2, 0, Math.PI / 2]} />
        <meshStandardMaterial color="#0a0b0e" roughness={0.4} metalness={0.45} />
      </mesh>

      {/* Side ribbing */}
      {[-1, 0, 1].map((edge) => (
        <group key={edge} rotation={[0, (edge * Math.PI * 2) / 3, 0]}>
          {Array.from({ length: 7 }, (_, i) => {
            const t = (i - 3) / 3;
            return (
              <mesh
                key={i}
                position={[t * SHIP_SIZE * 0.28, 0, -SHIP_SIZE * 0.22]}
              >
                <boxGeometry args={[0.08, SHIP_THICK * 0.85, 0.22]} />
                <meshStandardMaterial color="#0f1216" roughness={0.6} metalness={0.25} />
              </mesh>
            );
          })}
        </group>
      ))}

      {/* Bottom lights — central + three corners */}
      <mesh position={[0, -SHIP_THICK / 2 - 0.02, 0.05]} rotation={[-Math.PI / 2, 0, 0]}>
        <circleGeometry args={[0.85, 3]} />
        <meshStandardMaterial
          color="#ff6a2a"
          emissive="#ff4a10"
          emissiveIntensity={glow}
          roughness={0.3}
        />
      </mesh>
      {(
        [
          [0, SHIP_SIZE * 0.38],
          [SHIP_SIZE * 0.34, -SHIP_SIZE * 0.28],
          [-SHIP_SIZE * 0.34, -SHIP_SIZE * 0.28],
        ] as const
      ).map(([x, z], i) => (
        <mesh key={i} position={[x, -SHIP_THICK / 2 - 0.02, z]} rotation={[-Math.PI / 2, 0, 0]}>
          <circleGeometry args={[0.28, 16]} />
          <meshStandardMaterial
            color="#ff7a35"
            emissive="#ff5018"
            emissiveIntensity={glow}
            roughness={0.3}
          />
        </mesh>
      ))}
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
  const mode = useRef<FlightMode>("away");
  const yaw = useRef(DOCK_YAW);
  const pitch = useRef(0);
  const progress = useRef(0);
  const lastPhase = useRef(ship.phase);
  const style = useRef<TakeoffStyle>(takeoffStyle(ship.id));
  const [thrustersOn, setThrustersOn] = useState(false);
  const dock = dockPose(ship.dockIndex, index, edgeZ, bayCount);
  const fill =
    ship.capacity > 0 ? Math.max(0, Math.min(1, ship.loaded / ship.capacity)) : 0;

  useEffect(() => {
    const prev = lastPhase.current;
    const next = ship.phase;
    lastPhase.current = next;
    style.current = takeoffStyle(ship.id);

    if (next === "Approaching" && prev !== "Approaching") {
      mode.current = "arriving_hover";
      progress.current = 0;
      yaw.current = APPROACH_YAW; // point forward into travel (−Z toward pad)
      pitch.current = 0;
      if (ref.current) {
        ref.current.position.set(dock.x, dock.y + 16, dock.z + 28);
        ref.current.visible = true;
      }
    } else if (
      (next === "Departing" || next === "Away") &&
      (prev === "Loading" || prev === "Docked" || prev === "Unloading" || prev === "Departing")
    ) {
      if (
        mode.current === "docked" ||
        mode.current === "arriving_lower" ||
        mode.current === "arriving_rotate" ||
        mode.current === "arriving_hover"
      ) {
        mode.current = "depart_rise";
        progress.current = 0;
      }
    } else if (next === "Loading" || next === "Docked" || next === "Unloading") {
      if (mode.current === "away" || mode.current === "depart_fly") {
        mode.current = "arriving_hover";
        progress.current = 0;
      } else if (
        mode.current !== "arriving_hover" &&
        mode.current !== "arriving_rotate" &&
        mode.current !== "arriving_lower"
      ) {
        mode.current = "docked";
      }
    }
  }, [ship.phase, ship.id, dock.x, dock.y, dock.z]);

  useFrame((_, delta) => {
    const g = ref.current;
    if (!g) return;
    const dt = delta * Math.max(0.35, simSpeed);
    const active = mode.current !== "away" && mode.current !== "docked";
    if (active !== thrustersOn) {
      setThrustersOn(active || mode.current === "docked");
    }

    const applyPose = () => {
      g.rotation.y = yaw.current;
      g.rotation.x = pitch.current;
    };

    if (mode.current === "docked") {
      g.position.x += (dock.x - g.position.x) * Math.min(1, 3.5 * dt);
      g.position.y += (dock.y - g.position.y) * Math.min(1, 3.5 * dt);
      g.position.z += (dock.z - g.position.z) * Math.min(1, 3.5 * dt);
      yaw.current += (DOCK_YAW - yaw.current) * Math.min(1, 3.5 * dt);
      pitch.current += (0 - pitch.current) * Math.min(1, 3.5 * dt);
      // Gentle hover bob
      g.position.y = dock.y + Math.sin(performance.now() / 700) * 0.08;
      applyPose();
      g.visible = true;
      return;
    }

    if (mode.current === "arriving_hover") {
      progress.current = Math.min(1, progress.current + dt * 0.4);
      const t = progress.current;
      const ease = 1 - (1 - t) * (1 - t);
      g.position.set(
        dock.x,
        dock.y + 16 * (1 - ease) + 4 * (1 - ease * 0.5),
        dock.z + 28 * (1 - ease),
      );
      yaw.current = APPROACH_YAW;
      pitch.current = 0;
      applyPose();
      g.visible = true;
      if (t >= 1) {
        mode.current = "arriving_rotate";
        progress.current = 0;
      }
      return;
    }

    if (mode.current === "arriving_rotate") {
      progress.current = Math.min(1, progress.current + dt * 0.55);
      const t = progress.current;
      // Rotate flat-back toward warehouse (point away).
      yaw.current = APPROACH_YAW + (DOCK_YAW - APPROACH_YAW) * t;
      g.position.set(dock.x, dock.y + 4, dock.z);
      pitch.current = 0;
      applyPose();
      g.visible = true;
      if (t >= 1) {
        mode.current = "arriving_lower";
        progress.current = 0;
      }
      return;
    }

    if (mode.current === "arriving_lower") {
      progress.current = Math.min(1, progress.current + dt * 0.5);
      const t = progress.current;
      const ease = t * t;
      g.position.set(dock.x, dock.y + 4 * (1 - ease), dock.z);
      // Slight rear-down pitch toward warehouse while settling.
      pitch.current = 0.18 * Math.sin(t * Math.PI);
      yaw.current = DOCK_YAW;
      applyPose();
      g.visible = true;
      if (t >= 1) {
        mode.current = "docked";
        pitch.current = 0;
      }
      return;
    }

    if (mode.current === "depart_rise") {
      progress.current = Math.min(1, progress.current + dt * 0.45);
      const t = progress.current;
      const climb = style.current === "vtol" ? 18 : 7;
      g.position.set(dock.x, dock.y + climb * t, dock.z);
      pitch.current = -0.05 * t;
      yaw.current = DOCK_YAW;
      applyPose();
      g.visible = true;
      if (t >= 1) {
        mode.current = "depart_fly";
        progress.current = 0;
      }
      return;
    }

    if (mode.current === "depart_fly") {
      progress.current = Math.min(1, progress.current + dt * 0.28);
      const t = progress.current;
      const ease = t * t;
      if (style.current === "vtol") {
        // Pure vertical climb already done — translate away with thrusters.
        g.position.set(
          dock.x,
          dock.y + 18 + 6 * ease,
          dock.z + 40 * ease,
        );
      } else {
        // Climb-and-go diagonal.
        g.position.set(
          dock.x,
          dock.y + 7 + 14 * ease,
          dock.z + 48 * ease,
        );
      }
      yaw.current = DOCK_YAW;
      pitch.current = -0.12 * ease;
      applyPose();
      g.visible = t < 0.97;
      if (t >= 1) {
        mode.current = "away";
        g.visible = false;
      }
      return;
    }

    g.visible = false;
  });

  return (
    <group ref={ref} position={[dock.x, dock.y, dock.z]} visible={false}>
      <DoritoMesh thrustersOn={thrustersOn} />

      {/* Cargo fill bar above the slab */}
      <group position={[-SHIP_SIZE * 0.35, SHIP_THICK / 2 + 0.2, -SHIP_SIZE * 0.15]}>
        <mesh position={[SHIP_SIZE * 0.35, 0, 0]}>
          <boxGeometry args={[SHIP_SIZE * 0.7, 0.12, 0.28]} />
          <meshStandardMaterial color="#0a0c10" />
        </mesh>
        <mesh position={[(SHIP_SIZE * 0.7 * fill) / 2, 0, 0.01]}>
          <boxGeometry args={[SHIP_SIZE * 0.7 * fill || 0.001, 0.16, 0.32]} />
          <meshStandardMaterial color="#4edc7a" emissive="#0f5c2c" emissiveIntensity={0.45} />
        </mesh>
      </group>
      <Text
        position={[0, SHIP_THICK / 2 + 0.55, 0]}
        rotation={[-Math.PI / 2, 0, 0]}
        fontSize={0.4}
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
