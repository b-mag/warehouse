# Visual Management Tool - Design Brief

**One-line vision:** *RollerCoaster Tycoon, but for warehouse management.*

A pseudo-3D top-down operations view where you watch product cubes move into, through, and out of the warehouse in real time, and a management panel where you reach into the running system and change it - add workers until throughput plateaus, retune slotting, or inject chaos and watch the engine react and recover.

This brief refines Requirement 24 (Game Visualization Layer) with the concrete product direction agreed during brainstorming. It does not change the architecture: the Game stays a pure renderer + operator-control surface, and all authoritative behavior stays in the WMS_Core, driven by the Simulation.

---

## 1. Guiding Principles

1. **The simulation is the authoritative world; the tool is a window plus a control panel.** The Game renders authoritative state from the Contracts DTOs and triggers changes through the Api. It computes no business rules and generates no authoritative demand.
2. **Watching is the first product; steering is the second.** First you *see* the warehouse work (cubes on paths, docks, starships). Then you *poke* it (parameters, chaos) and observe the consequences through the same live view and metrics.
3. **Consequences must be real, not scripted.** When you change something, the WMS_Core reacts through its normal rules (FEFO shortfalls, backlog growth, contention, recovery). Nothing is faked in the client. This is what proves the engine is a real WMS and not a puppet.
4. **Glanceable over flashy.** Readable operations view for Phase 1: clarity of flow and bottlenecks beats visual effects.

---

## 2. The View (pseudo-3D, top-down)

Fidelity target for Phase 1: **Readable operations view** (extruded pseudo-3D, smooth interpolation, no heavy animation/particle work yet).

- **Perspective:** top-down / slight isometric, RollerCoaster-Tycoon style. Presented in 2D but with the illusion of depth.
- **Pseudo-3D rule (from your notes):** shapes are extruded, not flat. A square becomes a cube, a triangle becomes a pyramid/prism, shelves and dock bays have visible depth. Depth is chosen by object context.
- **Entity mapping (Req 24.2):**
  - Gel_Lots / pallets -> **cubes** (color or label by gel type; stack height can hint quantity).
  - Temperature_Zones -> **colored floor regions** (tint by zone temperature band; a zone in excursion is visually distinct).
  - Dock_Bays -> depth blocks at the warehouse edge; show occupied vs. free.
  - Starships -> **larger shapes** parked at docks during loading windows.
  - Agents (workers/forklifts) -> **moving markers** traversing their planned Path cell-by-cell.
- **Real-time movement:** agents move smoothly along paths. The client interpolates between authoritative state updates for smoothness but never invents position - the authoritative cell/path/speed come from the snapshot + SignalR deltas (`AgentDto` carries position, path cells, cells-per-second).
- **Contention made visible:** when an agent is held at a reserved path segment or waiting on a single-occupancy dock/pick face, show it (a hold indicator / color). Congestion is a first-class thing to *see*, because spotting bottlenecks is the point.

### Data the view already has
The read-only snapshot (`GetSimulationSnapshotHandler` -> `SimulationSnapshotDto`) already carries zones, lots, agents (with path + speed), starships (with loading windows), metrics, and operator parameters. The real-time channel streams updates. **No new rendering data is required for the base view.**

---

## 3. The Management Panel

### 3.1 Live parameter tuning (already supported end-to-end)
All six operator parameters already exist in the WMS_Core (`UpdateOperatorParameterHandler` / `OperatorParameterService`), are validated, applied to the live system, and published to clients. The panel exposes:

- Simulation speed (real-time or accelerated)
- **Workers on shift** - the headline "add workers until you see a bottleneck" lever
- Number of open dock bays
- Inbound arrival rate
- Colony demand multiplier
- Slotting strategy (velocity/affinity vs. naive first-available)

**Signature demo:** raise workers-on-shift step by step and watch `WarehouseMetrics` throughput climb - then plateau - as the bottleneck moves from labor to docks or storage. The plateau is real (backlog + throughput are authoritative), which viscerally teaches the bottleneck concept.

### 3.2 Chaos injection (new capability)
A "chaos" menu lets the operator inject disruptions and watch the system absorb them.

**Phase 1 scope (confirmed):**
- **Wipe out a colony's inventory / spike its demand** - suddenly zero a colony's on-hand or spike its demand, then watch outbound backlog surge and FEFO scramble to fulfill from remaining non-expired stock, with shortfalls reported honestly.
- **Dock / worker outage** - take dock bays or workers offline mid-run and watch the bottleneck shift and backlogs build, then recover when resources return.

**Deferred chaos (documented for later):** cold-chain failure (forced excursions), inbound supply flood. The seams (temperature readings, arrival rate) already exist, so these are additive.

**Architecture rule for chaos (important):** chaos events are **commands, not client-side effects**. The flow is:

```
Game (chaos menu)  --Api-->  Simulation driver  --command gateway-->  WMS_Core rules react
```

The Game only *requests* a disruption. The Simulation driver translates it into real commands (e.g. adjust demand generation, submit a large order, close a dock) issued through `IWarehouseCommandGateway`. The WMS_Core reacts through its existing rules. This keeps the Game a pure control surface and makes the reaction authentic and reproducible.

### 3.3 Colony inspector
Open a list of colonies, inspect each colony's current demand profile, on-order quantities, and fulfillment health, and select one as the target of a chaos event. Read from Contracts DTOs; the "wipe/spike" action routes through the driver as in 3.2.

---

## 4. Live Metrics Overlay (the feedback environment)

A glanceable HUD driven by `WarehouseMetrics` and derived read-models:

- Inbound / outbound throughput (lots per simulated unit time)
- Receiving backlog and outbound backlog
- Dock utilization and contention count
- At-risk lots / active temperature excursions (also the MAUI monitor's focus)
- Accrued labor cost and per-worker utilization

**Worker-incentive metric (Phase 1, confirmed):** *Travel-adjusted productivity* - actual vs. expected planned-path traversal time per worker. This is the "hard to game" metric from the brainstorming notes: it rewards efficient, on-path selection rather than raw units/hour. It is a **read-model projection** over data the core already produces (planned-path traversal time via `AStarPathPlanner`, per-task labor via `LaborLedger`) - a new DTO + scoring projection, not a new business rule. Other incentive ideas (accuracy rate, flow/interrupted indicator, team contribution, level-up status) are documented as future read-models in `WMS_solutions.md`.

---

## 5. The MAUI Monitor (unchanged, read-only)

A read-only cold-chain monitor showing temperature excursions, at-risk lots, and expiry warnings from the Contracts DTOs. Issues no commands. This is the second screen that "earns its keep" during a cold-chain chaos event later.

---

## 6. What Is Already Built vs. New

| Capability | Status |
|---|---|
| Authoritative agent position + planned path + speed in snapshot | Built (`SimulationSnapshotDto` / `AgentDto`) |
| Zones, lots, starships, metrics, parameters in snapshot | Built |
| Six live operator parameters (validate + apply + publish) | Built (`UpdateOperatorParameterHandler`) |
| Backlog + throughput metrics (bottleneck observability) | Built (`WarehouseMetrics`) |
| Real-time distribution over SignalR | Infrastructure task (28-32) |
| Pseudo-3D top-down renderer (R3F) | Game task (37) - new |
| Management panel wired to the six parameters | Game task (37.3) - existing endpoints |
| Chaos injection (colony wipe/spike, dock/worker outage) | New: driver command + Api endpoint + menu |
| Travel-adjusted productivity read-model | New: Application read-model projection + DTO |

---

## 7. Why This Is the "Wow"

A viewer watching this tool sees a living warehouse, then watches an operator break it on purpose and watches the engine absorb the blow through real FEFO, real backlog, real recovery - with honest metrics the whole way. It demonstrates warehouse-management expertise and a genuinely reusable engine, not a scripted animation. That is the difference between "a game" and "a digital twin you can play with."
