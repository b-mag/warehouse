/**
 * Deterministic scene-layout helpers for the operations view (task 37.2).
 *
 * The DTOs carry no world coordinates for zones, so the renderer lays them out in a
 * stable grid derived purely from their order/id. This is a presentation concern only
 * — it invents no business state, just where to draw an authoritative entity. Agent
 * coordinates, by contrast, ARE authoritative (AgentDto.x/y) and are used as-is.
 *
 * Keep numeric constants aligned with Forge.Application.Simulation.VisualGridLayout.
 */

import type {
  GelLotDto,
  StarshipDto,
  TemperatureZoneDto,
} from "./contracts";

/** World units per zone cell in the scene. */
export const ZONE_SIZE = 8;
export const ZONE_GAP = 2;
export const ZONE_PITCH = ZONE_SIZE + ZONE_GAP;

/** Agent / rail grid (matches InMemoryTickStateProvider). */
export const GRID_WIDTH_CELLS = 32;
export const GRID_HEIGHT_CELLS = 32;
export const CELL_WORLD = 1.1;
export const FLOOR_TOP = 0.4;

export const GRID_CENTER_X = (GRID_WIDTH_CELLS - 1) / 2;
export const GRID_CENTER_Y = (GRID_HEIGHT_CELLS - 1) / 2;

/** Idle breakroom cells (crew wait here between tasks). */
export const IDLE_BAY = { minX: 22, maxX: 28, minY: 2, maxY: 5 } as const;

/** Maglev rail row. */
export const RAIL_Y = 0;

export function cellToWorld(x: number, y: number): [number, number] {
  return [(x - GRID_CENTER_X) * CELL_WORLD, (y - GRID_CENTER_Y) * CELL_WORLD];
}

/** A zone's computed floor placement in world space. */
export interface ZonePlacement {
  zone: TemperatureZoneDto;
  /** Grid column/row (deterministic from index). */
  col: number;
  row: number;
  /** World-space center on the floor plane (x, z). */
  centerX: number;
  centerZ: number;
}

/**
 * Lay zones out in a near-square grid, ordered by id for stability. The number of
 * columns is ceil(sqrt(n)) so the footprint stays compact and the same set of zones
 * always produces the same arrangement.
 */
export function layoutZones(zones: TemperatureZoneDto[]): ZonePlacement[] {
  const ordered = [...zones].sort((a, b) => a.id.localeCompare(b.id));
  const cols = Math.max(1, Math.ceil(Math.sqrt(ordered.length)));
  const rows = Math.max(1, Math.ceil(ordered.length / cols));
  const originX = (-(cols - 1) * ZONE_PITCH) / 2;
  const originZ = (-(rows - 1) * ZONE_PITCH) / 2;

  return ordered.map((zone, index) => {
    const col = index % cols;
    const row = Math.floor(index / cols);
    return {
      zone,
      col,
      row,
      centerX: originX + col * ZONE_PITCH,
      centerZ: originZ + row * ZONE_PITCH,
    };
  });
}

/** Warehouse slab that borders the train rail and ship terminal pads. */
export function warehouseFloorBounds(
  placements: ZonePlacement[],
  openDockBays: number,
): { centerX: number; centerZ: number; width: number; depth: number } {
  const [railX0, railZ] = cellToWorld(0, RAIL_Y);

  let minX = railX0;
  let maxX = cellToWorld(GRID_WIDTH_CELLS - 1, RAIL_Y)[0];
  let minZ = railZ - 2.2;
  let maxZ = railZ + 2.2;

  for (const p of placements) {
    minX = Math.min(minX, p.centerX - ZONE_SIZE / 2 - 1);
    maxX = Math.max(maxX, p.centerX + ZONE_SIZE / 2 + 1);
    minZ = Math.min(minZ, p.centerZ - ZONE_SIZE / 2 - 1);
    maxZ = Math.max(maxZ, p.centerZ + ZONE_SIZE / 2 + 1);
  }

  // Ship terminal pads sit on the high-Z edge.
  const shipEdgeZ =
    placements.reduce((m, p) => Math.max(m, p.centerZ), 0) + ZONE_SIZE * 1.5;
  const bayCount = Math.max(1, openDockBays);
  const spacing = 6;
  const padW = 5;
  const padD = 4;
  const originX = (-(bayCount - 1) * spacing) / 2;
  minX = Math.min(minX, originX - padW / 2 - 1);
  maxX = Math.max(maxX, originX + (bayCount - 1) * spacing + padW / 2 + 1);
  maxZ = Math.max(maxZ, shipEdgeZ + padD + 1.5);

  // Breakroom footprint.
  const [bayX0, bayZ0] = cellToWorld(IDLE_BAY.minX, IDLE_BAY.minY);
  const [bayX1, bayZ1] = cellToWorld(IDLE_BAY.maxX, IDLE_BAY.maxY);
  minX = Math.min(minX, bayX0 - 1, bayX1 - 1);
  maxX = Math.max(maxX, bayX0 + 1, bayX1 + 1);
  minZ = Math.min(minZ, bayZ0 - 1, bayZ1 - 1);
  maxZ = Math.max(maxZ, bayZ0 + 1, bayZ1 + 1);

  return {
    centerX: (minX + maxX) / 2,
    centerZ: (minZ + maxZ) / 2,
    width: maxX - minX,
    depth: maxZ - minZ,
  };
}

/** Temperature band classification used only to tint zone floors for readability. */
export type TempBand = "frozen" | "chilled" | "cool" | "ambient";

export function temperatureBand(zone: TemperatureZoneDto): TempBand {
  const mid = (zone.minC + zone.maxC) / 2;
  if (mid <= -18) return "frozen";
  if (mid <= 2) return "chilled";
  if (mid <= 10) return "cool";
  return "ambient";
}

/** Base floor tint per band (a readable cold→warm ramp). */
export const BAND_COLOR: Record<TempBand, string> = {
  frozen: "#2b5ea8",
  chilled: "#3d8bbf",
  cool: "#4aa88a",
  ambient: "#b58b3a",
};

/** Lot visual state → color (normal / at-risk amber / expired red). */
export function lotColor(lot: GelLotDto): string {
  if (lot.isExpired) return "#d13b3b";
  if (lot.atRisk) return "#e0a53a";
  return "#dfe6ee";
}

/** Group lots by their authoritative zoneId (null → unslotted). */
export function groupLotsByZone(
  lots: GelLotDto[],
): Map<string | null, GelLotDto[]> {
  const grouped = new Map<string | null, GelLotDto[]>();
  for (const lot of lots) {
    const key = lot.zoneId;
    const bucket = grouped.get(key);
    if (bucket) {
      bucket.push(lot);
    } else {
      grouped.set(key, [lot]);
    }
  }
  return grouped;
}

/** True while a starship has an active loading window at the given instant. */
export function isDocked(starship: StarshipDto, nowIso: number): boolean {
  return starship.windows.some((w) => {
    const start = Date.parse(w.start);
    const end = Date.parse(w.end);
    return (
      Number.isFinite(start) &&
      Number.isFinite(end) &&
      nowIso >= start &&
      nowIso <= end
    );
  });
}
