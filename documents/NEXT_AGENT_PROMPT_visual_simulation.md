# Handoff Prompt: Make Forge a AAA Warehouse Simulation (Visual + Behavioral)

> Paste this whole document to the next AI agent as the opening prompt. It contains the goal,
> the confirmed current state, the exact root causes already diagnosed, the target behavior, the
> open design decisions to confirm with the user, and a suggested execution order.

---

## 0. Who you are / how to work

You are continuing work on **Forge** (Food Organization and Resource Guidance Engine), a .NET 10
Warehouse Management System (WMS Core) + accelerated **Simulation** driver + a **Next.js / React
Three Fiber** visual "tycoon" tool. The WMS data loop already works. Your job is to make the
**visual simulation tell the story** and make the **on-screen operator controls actually take effect
live**. Think "RollerCoaster Tycoon, but a food warehouse."

Working norms the user expects:
- Verify every file write (this workspace has intermittently truncated PowerShell heredoc writes --
  use [System.IO.File]::WriteAllText with absolute paths and confirm with a follow-up grep/read).
- Build with `dotnet build Forge.sln -c Debug`. Frontend checks: from `src/Forge.Game/web` run
  `npx tsc --noEmit` and `npx eslint src --max-warnings=0` (NOT `next lint` -- deprecated in Next 16).
- Full `dotnet test` hangs ~90s on shutdown; prefer `--filter` targeted runs against
  `tests/Forge.Tests/Forge.Tests.csproj`.
- The engine locks its output DLLs while running. If a build fails with MSB3021/MSB3027 "file in
  use", the app is still running -- stop it (VS Code debug stop button) and
  `dotnet build-server shutdown` before rebuilding.
- Do NOT run dev servers with the execute tool (they block). Ask the user to run
  `dotnet run --project src/Forge.Api` and `cd src/Forge.Game/web; npm run dev` themselves.
---

## 1. Current state (CONFIRMED working)

- The WMS data loop functions: inbound arrivals create PutAway tasks; a **task-execution stage**
  (`TickStages.TaskExecution` in `src/Forge.Application/Simulation/`) assigns idle agents to
  PutAway/Pick tasks, routes them, and completes tasks on arrival. Receiving backlog visibly drains
  to 0 in the LIVE METRICS panel. 16/16 tick tests pass.
- Embedded Postgres works (persistent instance). Engine runs headless at http://localhost:5195.
- The web client renders an isometric floor of temperature-zone tiles, gel-lot cubes (instanced),
  cone markers for agents, and static shapes for starships. WebGL context-loss on refresh is fixed
  (StrictMode disabled + active canvas remount).

## 2. Current state (BROKEN / missing -- your work)

1. **Agents never appear to move.** ROOT CAUSE (confirmed): the client receives the full
   `SimulationSnapshotDto` **only once, on SignalR connect** (`SimulationHub.OnConnectedAsync`)
   plus a one-time cold REST fetch (`fetchSnapshot`). After that it only gets **incremental events**
   (BacklogChanged, LotExpired, TemperatureExcursion, OperatorParameterChanged). There is **NO
   periodic push of agent/starship positions**, so the renderer never learns agents moved.
   `SignalRStatePublisher.cs` subscribes only to domain events; the store reducer
   (`src/Forge.Game/web/src/lib/store.ts`) only patches lots/metrics/params. Agents/starships are
   frozen at connect-time values.

2. **Only ~6 agents show, but WORKERS ON SHIFT default is 25.** `InMemoryTickStateProvider` defaults
   `agentCount: 6`. Startup (`ForgeStartup.InstallSpatialStateAsync`) was changed to pass
   `agentCount = Math.Max(3, OperatorParameterState.WorkersOnShift)` -- verify it is in the running
   build and reflects 25.

3. **Agents can overlap.** Initial placement + movement do not visually enforce single-cell occupancy.

4. **No pallet / train / starship choreography.** DTOs carry no pallet-in-transit, no inbound train;
   starships are static (no arrive -> load -> depart). `AgentDto` has X, Y, PathCells, CellsPerSecond
   but no carried-cube state.

5. **On-screen controls do not take live effect.** Changing WORKERS ON SHIFT 25->10 changes nothing.
   Agent count is fixed at startup. User wants: lower count => surplus workers walk out and de-spawn;
   raise it => workers walk in and spawn.
## 3. Target behavior (the user's vision)

A readable, physical story on screen, matching how a real warehouse handles product:

1. **Inbound arrival = a train of pallet boxes.** 3D rectangular boxes (pallets) arrive on a
   track/conveyor into a receiving dock. Each box = one inbound pallet (a gel lot / PutAway task).
2. **A worker receives each pallet.** A worker walks to the arriving pallet, picks it up (the cube
   visually travels with the worker), carries it to its assigned storage area (temperature zone), and
   drops it -- the zone then shows that cube in storage.
3. **Outbound order = a worker picks the ordered cube.** When a Colony (a nationwide storefront)
   orders product, a worker walks to the storage zone holding it, takes the cube, and carries it to a
   dock.
4. **A starship arrives, loads, and departs.** At the dock a starship (see the user's reference image:
   a low-poly angular hull, wide at the rear tapering to a pointed nose) flies in, the queued outbound
   cubes load into it, and it flies away with the cargo. Use a stylized low-poly starship mesh
   matching the reference silhouette.
5. **Workers physically move and never overlap.** Smooth interpolated travel along aisles between
   ticks. Single-cell occupancy respected so two workers never stack.
6. **Live controls animate the world.** WORKERS ON SHIFT changes => workers spawn (walk in) or
   de-spawn (walk out and disappear). OPEN DOCK BAYS, INBOUND ARRIVAL RATE, COLONY DEMAND MULTIPLIER,
   SIMULATION SPEED, and SLOTTING STRATEGY should all produce a visible/behavioral response.
7. **"AAA" polish.** Smooth motion (interpolation between authoritative snapshots), clean low-poly
   art, subtle lighting where cheap, readable labels, a satisfying arrive/work/depart loop. Keep it
   performant (instanced rendering for many cubes; PRESERVE the WebGL context-loss fixes -- no
   per-frame GPU allocation, no module-level GPU singletons).

## 4. Architecture facts you must respect

- **Authoritative state lives in the WMS Core / Simulation; the Game is a pure renderer** (Req 2.4,
  24.9, 24.10). The Game must NOT compute business rules. New visual state (pallet-in-transit,
  carried cube, starship phase) must be derived from authoritative DTOs the engine sends, or be purely
  cosmetic interpolation of authoritative positions.
- **Determinism (Req 19.6):** simulation stages stay pure/deterministic -- order by id, no wall clock
  in stages, no RNG beyond seeded construction.
- **Layer boundaries (architecture tests, task 34):** Domain=BCL only; Application=Domain+Contracts;
  Simulation generates all inputs; Game references Contracts only.
- **Tick cadence:** loop interval 100ms wall; clock acceleration 60x; each tick ~6 simulated seconds;
  agent speed 1.5 cells/s => ~9 cells/tick. Raw positions jump; the client MUST interpolate between
  authoritative positions for smooth motion.
## 5. Root-cause fixes required (priority order)

### A. Stream live agent/starship state to the client (highest priority)
- Add a periodic positions push: extend `SignalRStatePublisher` (or a new hosted service) to
  broadcast a lightweight payload (agents + starships + carried-cube + phase) every N ms (100-250ms)
  via a new SignalR method (e.g. `PositionsUpdate`), with a matching store reducer action.
- OR have the client poll `GET /api/query/snapshot` on an interval (fastest to prove).
- Then the renderer interpolates positions between successive authoritative updates each frame
  (`useFrame` lerp) for smooth walking.

### B. Make agent count track WORKERS ON SHIFT live
- On `OperatorParameterChanged` for workers-on-shift, add/remove agents in
  `InMemoryTickStateProvider` (spawn at receiving dock and walk in; mark surplus agents "exiting" so
  they path to an exit and de-spawn on arrival). Add an agent lifecycle state
  (Spawning/Active/Exiting) on `AgentDto` so the renderer can animate it.
- Confirm the startup default spawns WorkersOnShift agents (not 6).

### C. Enforce single-cell occupancy
- Wire movement to the reservation ledger + single-occupancy registry so positions never collide;
  spread idle agents to distinct standby cells.

### D. Pallet / train / starship choreography
- Carried cube: add carried-lot indicator to `AgentDto` (e.g. CarryingLotId / CarryingKind), set
  while executing PutAway (inbound) or Pick (outbound).
- Inbound train: visible train/conveyor of boxes at the receiving dock, sized to arrival rate
  (cosmetic keyed off inbound events/backlog, or a small authoritative inbound-queue projection).
- Starship lifecycle: arrive -> dock -> load -> depart. Surface phase + dock on `StarshipDto`
  (Phase: Approaching|Docked|Loading|Departing, DockCell). Wire real outbound demand into
  `TickStages.StarshipLoading` (the demand resolver in `ApplyTickRulesHandler` is currently a stub
  returning null -- see the "no persisted starship->order line link" note).
- Replace cone markers with worker meshes, cubes for pallets, and a low-poly starship mesh matching
  the user's reference silhouette (angular hull, wide rear, pointed nose).

### E. Wire outbound so it visibly flows
- Colony orders -> Pick tasks -> worker picks ordered cube -> carries to dock -> starship loads ->
  departs. Confirm Pick tasks generate with usable destinations (PutAway/Pick currently use a
  placeholder dock cell (0,0); the tick stage spreads them via `WorkCellFor` for visual variety --
  replace with real zone-to-cell mapping when persisted).

## 6. Cleanup you MUST do (TEMP diagnostics from last session)

- `ApplyTickRulesHandler.cs`: remove the `[TICK-DIAG]` `Console.WriteLine` block and the
  `private long _diagTick;` field.
- `TickStages.cs` + `TickStages.TaskExecution.cs`: the extra diagnostic counters on
  `TaskExecutionOutcome` (Assigned, SkippedUnroutable, SkippedAssignFailed, InFlightNotArrived,
  QueueDepth) exist only for diagnosis -- keep or drop.
- `src/Forge.Api/appsettings.Development.json`: EF command logging was set to Warning to quiet the
  console. Keep that unless the user wants SQL logs back.
## 7. Questions to confirm with the user BEFORE building

Use the input tool; do not assume:
1. **Streaming approach:** periodic SignalR positions push (smoother, more work) vs. client polling the
   existing snapshot endpoint (fastest). Recommend the SignalR push for AAA feel.
2. **Update rate vs. determinism:** how often to send authoritative positions (100ms? 250ms?).
3. **Worker de-spawn animation:** confirm "surplus workers walk to an exit then disappear" and the
   exit location (receiving dock? building edge?).
4. **Authoritative vs. cosmetic visuals:** engine-owned pallet-in-transit + starship phases (more
   correct/testable) vs. cosmetic renderer keyed off events (faster). Recommend authoritative for the
   worker-carried cube + starship phase; inbound train can start cosmetic.
5. **Art direction:** low-poly style; worker representation (figure? forklift?); starship should match
   the provided reference silhouette.
6. **Scope of "controls work":** confirm the expected effect of each of the six operator parameters,
   e.g. OPEN DOCK BAYS = active dock lanes / starship berths; INBOUND ARRIVAL RATE = train
   frequency/length; COLONY DEMAND MULTIPLIER = outbound pick frequency; SIMULATION SPEED = animation +
   tick speed; SLOTTING STRATEGY = which zone pallets go to.

## 8. Key files (map)

Backend (C#):
- `src/Forge.Application/Simulation/ApplyTickRulesHandler.cs` -- per-tick pipeline orchestration.
- `src/Forge.Application/Simulation/TickStages.cs` -- movement + task-execution + loading stages.
- `src/Forge.Application/Simulation/TickStages.TaskExecution.cs` -- TaskExecutionOutcome record.
- `src/Forge.Application/Simulation/TickState.cs` -- tick-scoped state incl. AgentTasks link map.
- `src/Forge.Infrastructure/Adapters/InMemoryTickStateProvider.cs` -- live agents/grid/starships;
  agent spawn count; where live worker-count changes should add/remove agents.
- `src/Forge.Infrastructure/RealTime/SignalRStatePublisher.cs` -- event -> client push (add positions).
- `src/Forge.Api/Hubs/SimulationHub.cs` -- SignalR hub; initial snapshot on connect.
- `src/Forge.Api/Startup/ForgeStartup.cs` -- InstallSpatialStateAsync (agent count from WorkersOnShift).
- `src/Forge.Application/OperatorParameters/OperatorParameterState.cs` -- live operator params.
- `src/Forge.Contracts/Dtos/AgentDto.cs`, `StarshipDto.cs`, `SimulationSnapshotDto.cs` -- extend
  for carried-cube, agent lifecycle phase, starship phase/dock.

Frontend (TypeScript / R3F, `src/Forge.Game/web/src`):
- `lib/ForgeProvider.tsx` -- SignalR + REST wiring; subscribe to a new positions message here.
- `lib/store.ts` -- reducer; add a positions-update action; keep it a pure renderer.
- `lib/contracts.ts` -- TS mirrors of the DTOs; extend to match backend DTO changes.
- `components/scene/OperationsView.tsx` -- R3F Canvas (PRESERVE context-loss fixes: StrictMode off,
  no per-frame GPU allocation, no module-level GPU singletons, active remount on context loss).
- `components/scene/Agents.tsx`, `Lots.tsx`, `Starships.tsx`, `Zones.tsx` -- renderers; add
  interpolation (useFrame lerp), worker/pallet/starship meshes, spawn/de-spawn animation.
- `components/hud/OperatorPanel.tsx` -- operator controls (already POST changes through the Api).

## 9. Definition of done

- Workers = WORKERS ON SHIFT count, visible, spaced (no overlap), smoothly walking along aisles
  between receiving, storage zones, and docks.
- Inbound pallets arrive (train/conveyor); a worker carries each cube to its zone; the zone shows the
  stored cube.
- Colony orders cause a worker to pick the ordered cube and carry it to a dock; a starship arrives,
  loads it, and departs.
- Changing WORKERS ON SHIFT live spawns/de-spawns workers on screen (walk in / walk out).
- The other operator controls each produce a confirmed visible/behavioral effect.
- Backend builds clean, tick tests pass; frontend tsc --noEmit and eslint src --max-warnings=0 exit 0.
- All TEMP diagnostics removed. WebGL context-loss fixes preserved (no regressions on refresh).