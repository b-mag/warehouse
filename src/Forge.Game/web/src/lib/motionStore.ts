/**
 * High-frequency motion state kept OUT of the React reducer.
 *
 * PositionsUpdate arrives ~5 Hz. Putting agents/starships into forgeReducer forces the whole
 * Dashboard + R3F tree to reconcile every tick and climbs the JS heap. Scene components read
 * this store from useFrame (or a sparse useSyncExternalStore for spawn/despawn only).
 */

import type {
  AgentDto,
  PositionsUpdateDto,
  StarshipDto,
  TrainDto,
} from "./contracts";

export interface MotionState {
  agents: AgentDto[];
  starships: StarshipDto[];
  inboundQueueLotIds: string[];
  inTransitLotIds: string[];
  train: TrainDto | null;
  /** Authoritative sim clock ISO string when the engine sends it. */
  simulatedAt: string | null;
  /** Bumps only when agent/starship/train identity sets change (spawn/despawn). */
  identityVersion: number;
}

const empty: MotionState = {
  agents: [],
  starships: [],
  inboundQueueLotIds: [],
  inTransitLotIds: [],
  train: null,
  simulatedAt: null,
  identityVersion: 0,
};

let motion: MotionState = empty;
const identityListeners = new Set<() => void>();

function idsKey(
  agents: AgentDto[],
  starships: StarshipDto[],
  train: TrainDto | null,
): string {
  const a = agents.map((x) => x.id).join(",");
  const s = starships.map((x) => x.id).join(",");
  const t = train ? `${train.id}:${train.phase}:${train.carCount}` : "";
  return `${a}|${s}|${t}`;
}

let lastIds = "";

/** Latest motion payload — safe to read every animation frame. */
export function getMotion(): MotionState {
  return motion;
}

/** Subscribe to identity-only changes (agent/ship spawn or despawn). */
export function subscribeMotionIdentity(onStoreChange: () => void): () => void {
  identityListeners.add(onStoreChange);
  return () => {
    identityListeners.delete(onStoreChange);
  };
}

export function getMotionIdentityVersion(): number {
  return motion.identityVersion;
}

export function setMotionFromPositions(update: PositionsUpdateDto): void {
  const train = update.train ?? null;
  const nextIds = idsKey(update.agents, update.starships, train);
  const identityChanged = nextIds !== lastIds;
  if (identityChanged) {
    lastIds = nextIds;
  }

  motion = {
    agents: update.agents,
    starships: update.starships,
    inboundQueueLotIds: update.inboundQueueLotIds,
    inTransitLotIds: update.inTransitLotIds,
    train,
    simulatedAt: update.simulatedAt ?? motion.simulatedAt,
    identityVersion: identityChanged
      ? motion.identityVersion + 1
      : motion.identityVersion,
  };

  if (identityChanged) {
    identityListeners.forEach((l) => l());
  }
}

/** Seed motion from a full snapshot (connect / REST cold start). */
export function seedMotionFromSnapshot(snapshot: {
  agents: AgentDto[];
  starships: StarshipDto[];
  simulatedAt?: string | null;
  train?: TrainDto | null;
  inboundQueueLotIds?: string[];
  inTransitLotIds?: string[];
}): void {
  setMotionFromPositions({
    agents: snapshot.agents,
    starships: snapshot.starships,
    inboundQueueLotIds:
      snapshot.inboundQueueLotIds ?? motion.inboundQueueLotIds,
    inTransitLotIds: snapshot.inTransitLotIds ?? motion.inTransitLotIds,
    train: snapshot.train ?? motion.train,
    simulatedAt: snapshot.simulatedAt ?? null,
  });
}

export function setSimulatedAt(iso: string | null): void {
  if (iso === motion.simulatedAt) {
    return;
  }
  motion = { ...motion, simulatedAt: iso };
}
