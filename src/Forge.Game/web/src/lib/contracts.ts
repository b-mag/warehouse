/**
 * TypeScript mirrors of the Forge.Contracts DTOs (task 37.1; Req 2.1, 24.1).
 *
 * These types match the C# `Forge.Contracts` records field-for-field. ASP.NET Core's
 * System.Text.Json serializes with camelCase property names by default, so the JSON
 * fields the engine emits are camelCase (e.g. `zones`, `lots`, `minC`, `cellsPerSecond`).
 * C# `Guid` values serialize as lowercase-hyphenated strings; `DateTimeOffset` values
 * serialize as ISO-8601 strings. Both are represented here as `string`.
 *
 * The web client renders ONLY from these DTOs and computes no business rules
 * (Req 24.9, 24.10, 2.4).
 */

/** Mirrors Forge.Contracts.Dtos.CellDto. */
export interface CellDto {
  x: number;
  y: number;
}

/** Mirrors Forge.Contracts.Dtos.TemperatureZoneDto. */
export interface TemperatureZoneDto {
  id: string;
  minC: number;
  maxC: number;
  capacity: number;
  stored: number;
}

/** Mirrors Forge.Contracts.Dtos.GelLotDto. */
export interface GelLotDto {
  id: string;
  gelTypeId: string;
  /** ISO-8601 timestamp (C# DateTimeOffset). */
  expiresAt: string;
  quantity: number;
  isExpired: boolean;
  atRisk: boolean;
  zoneId: string | null;
}

/** Mirrors Forge.Contracts.Dtos.AgentDto. */
export interface AgentDto {
  id: string;
  x: number;
  y: number;
  pathCells: CellDto[];
  cellsPerSecond: number;
  phase: string;
  carryingLotId: string | null;
}

/** Mirrors Forge.Contracts.Dtos.LoadingWindowDto. */
export interface LoadingWindowDto {
  /** ISO-8601 timestamp (C# DateTimeOffset). */
  start: string;
  /** ISO-8601 timestamp (C# DateTimeOffset). */
  end: string;
}

/** Mirrors Forge.Contracts.Dtos.StarshipDto. */
export interface StarshipDto {
  id: string;
  capacity: number;
  loaded: number;
  destinationColony: string;
  windows: LoadingWindowDto[];
  phase: string;
  dockIndex: number;
}

/** Mirrors Forge.Contracts.Dtos.BacklogMetricsDto. */
export interface BacklogMetricsDto {
  receiving: number;
  outbound: number;
  inboundThroughput: number;
  outboundThroughput: number;
  dockContention: number;
  dockUtilization: number;
}

/** Mirrors Forge.Contracts.Dtos.OperatorParameterStateDto. */
export interface OperatorParameterStateDto {
  simSpeed: number;
  workersOnShift: number;
  openDockBays: number;
  inboundRate: number;
  demandMultiplier: number;
  slottingStrategy: string;
}

/** Mirrors Forge.Contracts.Dtos.SimulationSnapshotDto (Req 23.3). */
export interface SimulationSnapshotDto {
  zones: TemperatureZoneDto[];
  lots: GelLotDto[];
  agents: AgentDto[];
  starships: StarshipDto[];
  metrics: BacklogMetricsDto;
  parameters: OperatorParameterStateDto;
}

/** Positions payload pushed frequently for smooth interpolation (Req 23.4). */
export interface PositionsUpdateDto {
  agents: AgentDto[];
  starships: StarshipDto[];
  inboundQueueLotIds: string[];
  inTransitLotIds: string[];
}

/** Inventory projection for holding-area cubes / zone stored counts. */
export interface InventoryUpdateDto {
  zones: TemperatureZoneDto[];
  lots: GelLotDto[];
}

/** Mirrors Forge.Contracts.OperatorParameters.OperatorParameterDto (the PUT body). */
export interface OperatorParameterDto {
  key: string;
  value: string;
}

/**
 * Canonical operator-parameter keys — mirrors
 * Forge.Contracts.OperatorParameters.OperatorParameterKey (Req 20.1).
 */
export const OperatorParameterKey = {
  SimSpeed: "sim-speed",
  WorkersOnShift: "workers-on-shift",
  OpenDockBays: "open-dock-bays",
  InboundRate: "inbound-rate",
  DemandMultiplier: "demand-multiplier",
  SlottingStrategy: "slotting-strategy",
} as const;

export type OperatorParameterKeyValue =
  (typeof OperatorParameterKey)[keyof typeof OperatorParameterKey];

/**
 * Slotting-strategy keys — mirrors
 * Forge.Contracts.OperatorParameters.SlottingStrategyKey (Req 20.7).
 */
export const SlottingStrategyKey = {
  VelocityAffinity: "velocity-affinity",
  NaiveFirstAvailable: "naive-first-available",
} as const;

export type SlottingStrategyKeyValue =
  (typeof SlottingStrategyKey)[keyof typeof SlottingStrategyKey];

// ---------------------------------------------------------------------------
// Incremental real-time event payloads (Forge.Contracts.Events). These mirror
// the transport-event records forwarded by SignalRStatePublisher. The client
// applies them to its authoritative state store; it never derives them.
// ---------------------------------------------------------------------------

/** Mirrors Forge.Contracts.Events.LotExpiredEvent. */
export interface LotExpiredEvent {
  lotId: string;
  at: string;
}

/** Mirrors Forge.Contracts.Events.TemperatureExcursionEvent. */
export interface TemperatureExcursionEvent {
  lotId: string;
  celsius: number;
  at: string;
}

/** Mirrors Forge.Contracts.Events.BlockedArrivalEvent. */
export interface BlockedArrivalEvent {
  lotId: string;
  reason: string;
}

/** Mirrors Forge.Contracts.Events.BlockedPlacementEvent. */
export interface BlockedPlacementEvent {
  lotId: string;
  reason: string;
}

/** Mirrors Forge.Contracts.Events.BacklogChangedEvent. */
export interface BacklogChangedEvent {
  kind: string;
  newSize: number;
}

/** Mirrors Forge.Contracts.Events.OperatorParameterChangedEvent (Req 20.9). */
export interface OperatorParameterChangedEvent {
  state: OperatorParameterStateDto;
}

/** The colony-order request body accepted by POST /api/orders. */
export interface CreateColonyOrderLineRequest {
  gelTypeId: string;
  quantity: number;
}

export interface CreateColonyOrderRequest {
  colonyId: string;
  lines: CreateColonyOrderLineRequest[];
  deliveryWindowStart: string;
  deliveryWindowEnd: string;
}

export interface CreateColonyOrderResponse {
  orderId: string;
}
