"use client";

/**
 * Starships as larger shapes docked at the warehouse edge during loading windows
 * (task 37.2; Req 24.2).
 *
 * A starship with an active loading window (now within [start, end]) is drawn parked at
 * the docking edge; otherwise it sits further out, dimmed. A fill bar shows loaded vs.
 * capacity. The docked/undocked decision reads the authoritative windows verbatim.
 */

import { Text } from "@react-three/drei";
import { useMemo, useState } from "react";

import type { StarshipDto } from "@/lib/contracts";
import { isDocked } from "@/lib/layout";
import { useSimClock } from "./useSimClock";

interface StarshipsProps {
  starships: StarshipDto[];
  /** Docking edge z-coordinate (front of the warehouse). */
  edgeZ: number;
}

const SHIP_W = 4;
const SHIP_H = 2.2;
const SHIP_D = 3;
const SPACING = 6;

export function Starships({ starships, edgeZ }: StarshipsProps) {
  const ordered = useMemo(
    () => [...starships].sort((a, b) => a.id.localeCompare(b.id)),
    [starships],
  );
  const originX = (-(ordered.length - 1) * SPACING) / 2;
  // "now" is a side-effectful clock; read it via a frame-driven hook, not during render.
  const [now, setNow] = useState<number>(() => 0);
  useSimClock(setNow);

  return (
    <group>
      {ordered.map((ship, index) => {
        const docked = isDocked(ship, now);
        const x = originX + index * SPACING;
        const z = docked ? edgeZ + SHIP_D : edgeZ + SHIP_D + 6;
        const fill =
          ship.capacity > 0
            ? Math.max(0, Math.min(1, ship.loaded / ship.capacity))
            : 0;
        return (
          <group key={ship.id} position={[x, SHIP_H / 2 + 0.4, z]}>
            {/* Hull: a larger extruded body. */}
            <mesh castShadow>
              <boxGeometry args={[SHIP_W, SHIP_H, SHIP_D]} />
              <meshStandardMaterial
                color={docked ? "#8a93a6" : "#4a5060"}
                emissive={docked ? "#2b6cff" : "#000000"}
                emissiveIntensity={docked ? 0.25 : 0}
                roughness={0.4}
                metalness={0.3}
                transparent
                opacity={docked ? 1 : 0.55}
              />
            </mesh>
            {/* Nose prism for a bit of ship silhouette. */}
            <mesh position={[0, 0, -SHIP_D / 2 - 0.6]} rotation={[Math.PI / 2, 0, 0]}>
              <coneGeometry args={[SHIP_W / 2, 1.4, 4]} />
              <meshStandardMaterial
                color={docked ? "#9aa3b6" : "#4a5060"}
                transparent
                opacity={docked ? 1 : 0.55}
              />
            </mesh>
            {/* Loaded/capacity fill bar along the hull top. */}
            <group position={[-SHIP_W / 2 + 0.2, SHIP_H / 2 + 0.15, 0]}>
              <mesh position={[SHIP_W / 2 - 0.2, 0, 0]}>
                <boxGeometry args={[SHIP_W - 0.4, 0.18, 0.4]} />
                <meshStandardMaterial color="#20252e" />
              </mesh>
              <mesh
                position={[((SHIP_W - 0.4) * fill) / 2, 0, 0.01]}
              >
                <boxGeometry args={[(SHIP_W - 0.4) * fill || 0.001, 0.24, 0.5]} />
                <meshStandardMaterial
                  color="#4edc7a"
                  emissive="#0f5c2c"
                  emissiveIntensity={0.4}
                />
              </mesh>
            </group>
            <Text
              position={[0, SHIP_H / 2 + 0.7, 0]}
              rotation={[-Math.PI / 2, 0, 0]}
              fontSize={0.6}
              color="#f2f6fb"
              anchorX="center"
              anchorY="middle"
            >
              {`${ship.loaded}/${ship.capacity}${docked ? " · loading" : ""}`}
            </Text>
          </group>
        );
      })}
    </group>
  );
}
