"use client";

/**
 * Agents (workers / forklifts) as moving markers traversing their planned path
 * (task 37.2; Req 24.2, 24.3).
 *
 * INTERPOLATION RULE (Req 24.3): between authoritative updates the marker glides along
 * its `pathCells` at `cellsPerSecond` for smoothness, but it NEVER invents a destination —
 * it only advances toward cells the engine already planned, and it SNAPS to the
 * authoritative `(x, y)` whenever a new snapshot/update arrives. When an agent's
 * authoritative position holds still while it still has a path ahead, it is drawn with a
 * contention (hold) indicator, because spotting bottlenecks is the point.
 */

import { useFrame } from "@react-three/fiber";
import { useEffect, useMemo, useRef, useState } from "react";
import type { Group } from "three";

import type { AgentDto, CellDto } from "@/lib/contracts";

/** World units per agent grid cell, and the y-height markers float at. */
const CELL_WORLD = 1.1;
const MARKER_Y = 1.2;

/** Map an agent grid cell to a world-space (x, z) position centered on the origin. */
function cellToWorld(x: number, y: number): [number, number] {
  return [x * CELL_WORLD, y * CELL_WORLD];
}

interface AgentMarkerProps {
  agent: AgentDto;
  /** Bumped whenever a fresh authoritative snapshot arrives (forces a snap). */
  snapSeq: number;
}

function AgentMarker({ agent, snapSeq }: AgentMarkerProps) {
  const ref = useRef<Group>(null);
  const holdRef = useRef<Group>(null);

  // The authoritative path, from the agent's current cell through its planned cells.
  const path = useMemo<CellDto[]>(() => {
    const cells: CellDto[] = [{ x: agent.x, y: agent.y }];
    for (const c of agent.pathCells) {
      const last = cells[cells.length - 1];
      if (c.x !== last.x || c.y !== last.y) {
        cells.push(c);
      }
    }
    return cells;
  }, [agent.x, agent.y, agent.pathCells]);

  // Progress along `path` in units of cells; reset to the authoritative start on snap.
  const progress = useRef(0);
  const lastAuthoritative = useRef({ x: agent.x, y: agent.y });

  // Detect whether the agent is advancing (moving) vs. held (contention). Held state is
  // rendered (marker color + hold ring), so it lives in component state; the frame loop
  // and effect keep a ref mirror to avoid reading state during render.
  const [held, setHeld] = useState(false);
  const isHeld = useRef(false);

  useEffect(() => {
    // A new authoritative snapshot: snap position to (x, y), and detect a hold —
    // if the authoritative cell did not change but a path still remains, the agent is
    // being held at a reserved segment / single-occupancy resource (contention).
    const prev = lastAuthoritative.current;
    const stalled = prev.x === agent.x && prev.y === agent.y;
    const nextHeld = stalled && agent.pathCells.length > 0;
    isHeld.current = nextHeld;
    setHeld(nextHeld);
    lastAuthoritative.current = { x: agent.x, y: agent.y };
    progress.current = 0;
  }, [snapSeq, agent.x, agent.y, agent.pathCells.length]);

  useFrame((_, delta) => {
    const group = ref.current;
    if (!group) {
      return;
    }

    // Advance along the authoritative path only; do not exceed the last planned cell.
    const speed = agent.cellsPerSecond > 0 ? agent.cellsPerSecond : 0;
    if (speed > 0 && path.length > 1 && !isHeld.current) {
      progress.current = Math.min(
        progress.current + speed * delta,
        path.length - 1,
      );
    }

    const p = progress.current;
    const i = Math.floor(p);
    const frac = p - i;
    const a = path[Math.min(i, path.length - 1)];
    const b = path[Math.min(i + 1, path.length - 1)];
    const [ax, az] = cellToWorld(a.x, a.y);
    const [bx, bz] = cellToWorld(b.x, b.y);
    group.position.set(ax + (bx - ax) * frac, MARKER_Y, az + (bz - az) * frac);

    const hold = holdRef.current;
    if (hold) {
      hold.visible = isHeld.current;
      if (isHeld.current) {
        // A gentle pulse on the hold ring to draw the eye to congestion.
        const s = 1 + Math.sin(performance.now() / 200) * 0.15;
        hold.scale.set(s, 1, s);
      }
    }
  });

  return (
    <group ref={ref}>
      {/* Forklift/worker marker: an extruded prism (pseudo-3D) tinted by motion. */}
      <mesh castShadow>
        <coneGeometry args={[0.6, 1.4, 4]} />
        <meshStandardMaterial
          color={held ? "#f2c14e" : "#4ea1f2"}
          roughness={0.4}
          metalness={0.1}
        />
      </mesh>
      {/* Contention/hold indicator ring, shown only while the agent is held. */}
      <group ref={holdRef} visible={false} position={[0, -0.9, 0]}>
        <mesh rotation={[-Math.PI / 2, 0, 0]}>
          <ringGeometry args={[0.8, 1.1, 24]} />
          <meshBasicMaterial color="#ff5a3c" transparent opacity={0.85} />
        </mesh>
      </group>
    </group>
  );
}

interface AgentsProps {
  agents: AgentDto[];
  snapSeq: number;
}

export function Agents({ agents, snapSeq }: AgentsProps) {
  return (
    <group>
      {agents.map((agent) => (
        <AgentMarker key={agent.id} agent={agent} snapSeq={snapSeq} />
      ))}
    </group>
  );
}
