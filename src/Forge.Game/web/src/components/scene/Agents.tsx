"use client";

/**
 * Agents (workers / forklifts) as moving markers traversing their planned path
 * (task 37.2; Req 24.2, 24.3).
 *
 * INTERPOLATION RULE (Req 24.3): between authoritative updates the marker glides along
 * its remaining `pathCells` at `cellsPerSecond`. Path cells behind the agent are trimmed
 * so a full historical path never pulls the marker backward (teleport look).
 */

import { useFrame } from "@react-three/fiber";
import { useEffect, useMemo, useRef, useState } from "react";
import type { Group } from "three";

import type { AgentDto, CellDto } from "@/lib/contracts";
import { CELL_WORLD, GRID_CENTER_X, GRID_CENTER_Y } from "@/lib/layout";

const MARKER_Y = 1.2;
const CARRY_CUBE = 0.45;
const CARRY_CUBE_Y = 0.55;

function cellToWorld(x: number, y: number): [number, number] {
  return [(x - GRID_CENTER_X) * CELL_WORLD, (y - GRID_CENTER_Y) * CELL_WORLD];
}

/** Build a continuous remaining path starting at the authoritative cell. */
function remainingPath(agent: AgentDto): CellDto[] {
  const cells: CellDto[] = [];
  const path = agent.pathCells;
  let start = 0;
  for (let i = 0; i < path.length; i++) {
    if (path[i].x === agent.x && path[i].y === agent.y) {
      start = i;
      break;
    }
  }

  // If current cell is not in the path, begin from here then append future cells only
  // when they continue forward (skip any prefix that would jump backward).
  if (path.length === 0 || path[start].x !== agent.x || path[start].y !== agent.y) {
    cells.push({ x: agent.x, y: agent.y });
    for (const c of path) {
      const last = cells[cells.length - 1];
      const manhattan = Math.abs(c.x - last.x) + Math.abs(c.y - last.y);
      if (manhattan === 1) {
        cells.push(c);
      } else if (c.x === last.x && c.y === last.y) {
        continue;
      } else {
        // Discontinuity — stop; do not teleport through distant cells.
        break;
      }
    }
    return cells;
  }

  for (let i = start; i < path.length; i++) {
    const c = path[i];
    const last = cells[cells.length - 1];
    if (!last || c.x !== last.x || c.y !== last.y) {
      cells.push(c);
    }
  }
  return cells.length > 0 ? cells : [{ x: agent.x, y: agent.y }];
}

interface AgentMarkerProps {
  agent: AgentDto;
  simSpeed: number;
}

function AgentMarker({ agent, simSpeed }: AgentMarkerProps) {
  const ref = useRef<Group>(null);
  const holdRef = useRef<Group>(null);

  const path = useMemo(() => remainingPath(agent), [agent.x, agent.y, agent.pathCells]);

  const progress = useRef(0);
  const lastAuthoritative = useRef({ x: agent.x, y: agent.y });
  const visual = useRef({ x: agent.x, y: agent.y, frac: 0 });

  const [held, setHeld] = useState(false);
  const isHeld = useRef(false);
  const stallTicks = useRef(0);

  useEffect(() => {
    const prev = lastAuthoritative.current;
    const stalled = prev.x === agent.x && prev.y === agent.y;
    if (stalled && agent.pathCells.length > 1) {
      stallTicks.current += 1;
    } else {
      stallTicks.current = 0;
    }
    const nextHeld = stallTicks.current >= 8;
    isHeld.current = nextHeld;
    setHeld(nextHeld);

    // Soft-catch: if authority jumped more than one cell, ease from the last visual
    // position by rebuilding progress at 0 on the new remaining path (no hard snap mid-frame).
    const jump = Math.abs(agent.x - prev.x) + Math.abs(agent.y - prev.y);
    if (!stalled) {
      if (jump <= 1) {
        progress.current = 0;
      } else {
        // Multi-cell tick: start interpolating from previous visual toward new path[0].
        progress.current = 0;
        visual.current = { x: prev.x, y: prev.y, frac: 0 };
      }
    }

    lastAuthoritative.current = { x: agent.x, y: agent.y };
  }, [agent.x, agent.y, agent.pathCells.length]);

  useFrame((_, delta) => {
    const group = ref.current;
    if (!group) {
      return;
    }

    const speed = agent.cellsPerSecond > 0 ? agent.cellsPerSecond : 0;
    if (speed > 0 && path.length > 1 && !isHeld.current) {
      progress.current = Math.min(
        progress.current + speed * delta * simSpeed,
        path.length - 1,
      );
    }

    const p = progress.current;
    const i = Math.floor(p);
    const frac = p - i;
    const a = path[Math.min(i, path.length - 1)];
    const b = path[Math.min(i + 1, path.length - 1)];

    // If we had a multi-cell jump, blend previous visual into the new path head first.
    let ax = a.x;
    let ay = a.y;
    if (
      visual.current.x !== a.x ||
      visual.current.y !== a.y
    ) {
      const blend = Math.min(1, speed * delta * simSpeed * 2);
      ax = visual.current.x + (a.x - visual.current.x) * blend;
      ay = visual.current.y + (a.y - visual.current.y) * blend;
      if (Math.abs(ax - a.x) + Math.abs(ay - a.y) < 0.05) {
        visual.current = { x: a.x, y: a.y, frac: 0 };
        ax = a.x;
        ay = a.y;
      } else {
        visual.current = { x: ax, y: ay, frac: 0 };
      }
    }

    const [wx0, wz0] = cellToWorld(ax, ay);
    const [wx1, wz1] = cellToWorld(b.x, b.y);
    const t = visual.current.x === a.x && visual.current.y === a.y ? frac : 0;
    group.position.set(wx0 + (wx1 - wx0) * t, MARKER_Y, wz0 + (wz1 - wz0) * t);

    const hold = holdRef.current;
    if (hold) {
      hold.visible = isHeld.current;
      if (isHeld.current) {
        const s = 1 + Math.sin(performance.now() / 200) * 0.15;
        hold.scale.set(s, 1, s);
      }
    }
  });

  return (
    <group ref={ref}>
      <mesh castShadow>
        <coneGeometry args={[0.6, 1.4, 4]} />
        <meshStandardMaterial
          color={held ? "#f2c14e" : "#4ea1f2"}
          roughness={0.4}
          metalness={0.1}
        />
      </mesh>

      {agent.carryingLotId ? (
        <mesh position={[0, CARRY_CUBE_Y, 0]}>
          <boxGeometry args={[CARRY_CUBE, CARRY_CUBE, CARRY_CUBE]} />
          <meshStandardMaterial color="#4edc7a" roughness={0.5} metalness={0.05} />
        </mesh>
      ) : null}

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
  simSpeed: number;
}

export function Agents({ agents, simSpeed }: AgentsProps) {
  return (
    <group>
      {agents.map((agent) => (
        <AgentMarker key={agent.id} agent={agent} simSpeed={simSpeed} />
      ))}
    </group>
  );
}
