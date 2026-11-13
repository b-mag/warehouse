"use client";

/**
 * Agents as moving markers that WALK cell-to-cell (RCT-style), never snap to authority.
 *
 * The engine advances many cells per tick at high sim-speed. PositionsUpdate carries the
 * jumped cell + remaining path. This client keeps a continuous visual position and chases
 * along a reconstructed walk path so the eye always sees travel, not teleportation.
 */

import { useFrame } from "@react-three/fiber";
import { useMemo, useRef, useSyncExternalStore } from "react";
import type { Group } from "three";

import type { AgentDto, CellDto } from "@/lib/contracts";
import { CELL_WORLD, GRID_CENTER_X, GRID_CENTER_Y } from "@/lib/layout";
import {
  getMotion,
  getMotionIdentityVersion,
  subscribeMotionIdentity,
} from "@/lib/motionStore";

const MARKER_Y = 1.2;
const CARRY_CUBE = 0.45;
const CARRY_CUBE_Y = 0.55;

/** Cap presentation walk speed (cells/sec) so motion stays readable even at 60× sim. */
const MAX_VISUAL_CELLS_PER_SEC = 10;
/** Soft floor so agents never crawl when nearly caught up. */
const MIN_VISUAL_CELLS_PER_SEC = 1.2;

type Pt = { x: number; y: number };

function cellToWorld(x: number, y: number): [number, number] {
  return [(x - GRID_CENTER_X) * CELL_WORLD, (y - GRID_CENTER_Y) * CELL_WORLD];
}

/** Orthogonal cell steps from A to B (exclusive of A, inclusive of B). */
function manhattanSteps(from: Pt, to: Pt): Pt[] {
  const steps: Pt[] = [];
  let x = Math.round(from.x);
  let y = Math.round(from.y);
  const tx = Math.round(to.x);
  const ty = Math.round(to.y);
  while (x !== tx || y !== ty) {
    if (x !== tx) {
      x += Math.sign(tx - x);
    } else {
      y += Math.sign(ty - y);
    }
    steps.push({ x, y });
  }
  return steps;
}

/**
 * Authoritative remaining route from the engine cell along pathCells.
 * Does not invent business rules — only orders DTO cells for drawing.
 */
function remainingFromAuthority(agent: AgentDto): CellDto[] {
  const path = agent.pathCells;
  let start = 0;
  for (let i = 0; i < path.length; i++) {
    if (path[i].x === agent.x && path[i].y === agent.y) {
      start = i;
      break;
    }
  }

  const cells: CellDto[] = [{ x: agent.x, y: agent.y }];
  if (path.length === 0 || path[start]?.x !== agent.x || path[start]?.y !== agent.y) {
    for (const c of path) {
      const last = cells[cells.length - 1];
      const manhattan = Math.abs(c.x - last.x) + Math.abs(c.y - last.y);
      if (manhattan === 1) {
        cells.push(c);
      } else if (c.x === last.x && c.y === last.y) {
        continue;
      } else {
        break;
      }
    }
    return cells;
  }

  for (let i = start + 1; i < path.length; i++) {
    const c = path[i];
    const last = cells[cells.length - 1];
    if (c.x !== last.x || c.y !== last.y) {
      cells.push(c);
    }
  }
  return cells;
}

/**
 * Walk path for the renderer: from current visual → authority (fills jumped cells) →
 * remaining planned cells. Visual never starts at the jumped authority cell.
 */
function buildChasePath(visual: Pt, agent: AgentDto): Pt[] {
  const auth: Pt = { x: agent.x, y: agent.y };
  const after = remainingFromAuthority(agent);
  const bridge = manhattanSteps(visual, auth);

  const out: Pt[] = [{ x: visual.x, y: visual.y }];
  for (const c of bridge) {
    out.push(c);
  }
  // Append planned cells after authority (skip duplicate auth head).
  for (let i = 1; i < after.length; i++) {
    const c = after[i];
    const last = out[out.length - 1];
    if (c.x !== last.x || c.y !== last.y) {
      out.push(c);
    }
  }
  return out;
}

function nearestProgress(path: Pt[], visual: Pt): number {
  if (path.length <= 1) {
    return 0;
  }
  let bestI = 0;
  let bestD = Infinity;
  for (let i = 0; i < path.length - 1; i++) {
    const a = path[i];
    const b = path[i + 1];
    const abx = b.x - a.x;
    const aby = b.y - a.y;
    const len2 = abx * abx + aby * aby || 1;
    let t = ((visual.x - a.x) * abx + (visual.y - a.y) * aby) / len2;
    t = Math.max(0, Math.min(1, t));
    const px = a.x + abx * t;
    const py = a.y + aby * t;
    const d = (visual.x - px) ** 2 + (visual.y - py) ** 2;
    if (d < bestD) {
      bestD = d;
      bestI = i + t;
    }
  }
  return bestI;
}

interface AgentMarkerProps {
  agentId: string;
  simSpeed: number;
}

function AgentMarker({ agentId, simSpeed }: AgentMarkerProps) {
  const ref = useRef<Group>(null);
  const holdRef = useRef<Group>(null);
  const carryRef = useRef<Group>(null);

  const progress = useRef(0);
  const visual = useRef<Pt>({ x: 0, y: 0 });
  const pathRef = useRef<Pt[]>([{ x: 0, y: 0 }]);
  const lastAuthKey = useRef("");
  const stallTicks = useRef(0);
  const isHeld = useRef(false);
  const seeded = useRef(false);
  const lastAuthPos = useRef({ x: 0, y: 0 });

  useFrame((_, delta) => {
    const group = ref.current;
    if (!group) {
      return;
    }

    const agent = getMotion().agents.find((a) => a.id === agentId);
    if (!agent) {
      group.visible = false;
      return;
    }
    group.visible = true;

    if (!seeded.current) {
      visual.current = { x: agent.x, y: agent.y };
      pathRef.current = [{ x: agent.x, y: agent.y }];
      progress.current = 0;
      lastAuthPos.current = { x: agent.x, y: agent.y };
      lastAuthKey.current = `${agent.x},${agent.y},${agent.pathCells.length}`;
      seeded.current = true;
    }

    const authKey = `${agent.x},${agent.y},${agent.pathCells.map((c) => `${c.x}:${c.y}`).join(";")}`;
    if (authKey !== lastAuthKey.current) {
      const moved =
        agent.x !== lastAuthPos.current.x || agent.y !== lastAuthPos.current.y;
      if (moved && agent.pathCells.length > 1) {
        stallTicks.current = 0;
      } else if (!moved && agent.pathCells.length > 1) {
        stallTicks.current += 1;
      } else {
        stallTicks.current = 0;
      }
      isHeld.current = stallTicks.current >= 10;

      pathRef.current = buildChasePath(visual.current, agent);
      progress.current = nearestProgress(pathRef.current, visual.current);
      lastAuthKey.current = authKey;
      lastAuthPos.current = { x: agent.x, y: agent.y };
    }

    const path = pathRef.current;
    const base = agent.cellsPerSecond > 0 ? agent.cellsPerSecond : MIN_VISUAL_CELLS_PER_SEC;

    // Cells still ahead on the chase path (includes catch-up over jumped cells).
    const ahead = Math.max(0, path.length - 1 - progress.current);
    // Mild sim-speed influence so high acceleration feels busier, but never RCT-blurry.
    const paced = base * Math.min(Math.max(simSpeed, 1), 2.5);
    // Catch up over jumped cells within ~0.4s of wall time so motion stays continuous.
    const catchUp = ahead > 1.5 ? ahead / 0.4 : 0;
    const speed = Math.min(
      MAX_VISUAL_CELLS_PER_SEC,
      Math.max(MIN_VISUAL_CELLS_PER_SEC, paced, catchUp),
    );

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
    const vx = a.x + (b.x - a.x) * frac;
    const vy = a.y + (b.y - a.y) * frac;
    visual.current = { x: vx, y: vy };

    const [wx0, wz0] = cellToWorld(a.x, a.y);
    const [wx1, wz1] = cellToWorld(b.x, b.y);
    group.position.set(wx0 + (wx1 - wx0) * frac, MARKER_Y, wz0 + (wz1 - wz0) * frac);

    // Face travel direction
    if (Math.abs(b.x - a.x) + Math.abs(b.y - a.y) > 0.01) {
      group.rotation.y = Math.atan2(b.x - a.x, b.y - a.y);
    }

    const hold = holdRef.current;
    if (hold) {
      hold.visible = isHeld.current;
      if (isHeld.current) {
        const s = 1 + Math.sin(performance.now() / 200) * 0.15;
        hold.scale.set(s, 1, s);
      }
    }

    const carry = carryRef.current;
    if (carry) {
      carry.visible = !!agent.carryingLotId;
    }
  });

  return (
    <group ref={ref}>
      <mesh>
        <coneGeometry args={[0.6, 1.4, 4]} />
        <meshStandardMaterial color="#4ea1f2" roughness={0.4} metalness={0.1} />
      </mesh>

      <group ref={carryRef} visible={false} position={[0, CARRY_CUBE_Y, 0]}>
        <mesh>
          <boxGeometry args={[CARRY_CUBE, CARRY_CUBE, CARRY_CUBE]} />
          <meshStandardMaterial color="#4edc7a" roughness={0.5} metalness={0.05} />
        </mesh>
      </group>

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
  simSpeed: number;
}

export function Agents({ simSpeed }: AgentsProps) {
  const identityVersion = useSyncExternalStore(
    subscribeMotionIdentity,
    getMotionIdentityVersion,
    getMotionIdentityVersion,
  );

  const agentIds = useMemo(
    () => getMotion().agents.map((a) => a.id),
    [identityVersion],
  );

  return (
    <group>
      {agentIds.map((id) => (
        <AgentMarker key={id} agentId={id} simSpeed={simSpeed} />
      ))}
    </group>
  );
}
