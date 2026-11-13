# Handoff Prompt: Future Forge Features (post cargo/clock/perf slice)

> Paste this to a future agent when continuing the tycoon / WMS wow layer.
> Items already landed in the performance + cargo choreography + sim-clock slice are marked **Done** so they are not re-built.

---

## 0. Working norms

- Verify writes (PowerShell heredocs can truncate — prefer file tools / `WriteAllText`).
- Build: `dotnet build Forge.sln -c Debug`. Frontend: `npx tsc --noEmit` and `npx eslint src --max-warnings=0` from `src/Forge.Game/web`.
- Prefer filtered `dotnet test --filter …` (full suite can hang ~90s on shutdown).
- Engine locks DLLs while running — stop the Api before rebuild if MSB3021.
- Do **not** start long-lived dev servers from the agent execute tool; ask the user to run them.
- Architecture: Game is a pure renderer + operator controls; Simulation generates inputs; WMS Core owns rules.

---

## 1. Done in the cargo / clock / perf slice (do not re-fix)

- **PositionsUpdate off React render path** — `motionStore` + `useFrame` for agents/ships/train.
- **Slim PositionsUpdate** — empty starship `Windows` on the 200ms channel; skip unchanged fingerprints.
- **Inventory filter** — zero-qty lots excluded from InventoryUpdate / snapshot.
- **WebGL / SignalR hygiene** — listener remove, reconnect `clearTimeout`, `connection.off`.
- **Sim clock HUD** — authoritative `SimulatedAt` + day/night lighting.
- **Operator params** — `trains-per-day` (default 1), `ships-per-day` (default 4); sim-speed default aligned to 60×.
- **Inbound train lifecycle** — Away → Approaching → Unloading → Departing; multi-car; demand-weighted batch; depart when empty.
- **Starship multi-visit / idle depart** — ships-per-day spacing; partial depart after min dwell.

---

## 2. Unimplemented features (grouped by system)

### Chaos & scenarios

| Feature | Why it wows a WMS interviewer |
|---------|-------------------------------|
| Colony wipe / demand spike chaos | Shows FEFO, backlog, and recovery through real rules — not a scripted animation |
| Dock / worker outage chaos | Bottleneck shifts live when resources disappear |
| Scenario presets (Black Friday, cold-chain failure, dock outage) | One-click stress tests; watching stays primary |

Architecture: chaos is a **command** through the Api → Simulation driver → WMS Core (never client-faked).

### Inspection & HUD

| Feature | Why it wows |
|---------|-------------|
| Colony inspector | Demand profile, on-order, fulfillment health |
| Click-to-inspect agent / lot / ship | Path, carried lot, expiry, dock window — optional power-user |
| Forecast overlay UI | API exists; accept/override later — planning literacy |
| Travel-adjusted productivity | Hard-to-game labor metric (planned-path vs actual) |

### Flow visualization

| Feature | Why it wows |
|---------|-------------|
| Bottleneck heat (aisle / dock congestion) | Theory of constraints, glanceable |
| FEFO highlight | Show *why* this lot was picked vs newer stock |
| Wave / batch pick fan-in | Classic WMS vocabulary on screen |
| Slotting A/B ghost | Flip strategy → ghost alternate placement |
| Colony destination labels on ships | Outbound reads as distribution, not a drain |

### Labor & layout

| Feature | Why it wows |
|---------|-------------|
| Hard single-cell occupancy while moving | No stacked workers; reservation-ledger fidelity |
| Richer worker meshes (replace cones) | AAA polish without faking rules |
| Soft ops grade (OTIF / spoilage / labor cost) | Quiet score that drifts — observational, not game-over |

### Platform & cleanup

| Feature | Notes |
|---------|-------|
| MAUI cold-chain monitor | Second screen for excursions / at-risk lots |
| Leftover TEMP diagnostics | Strip `[TICK-DIAG]` / unused counters if still present |
| Placeholder pick destinations | Replace `(0,0)` / visual-only cells with real zone→cell mapping where still stubbed |

---

## 3. Suggested next slice (when the user asks)

1. **Chaos injection + colony inspector** — highest interview wow per the visual management brief.
2. **FEFO highlight + bottleneck heat** — passive visual literacy, no forced clicks.
3. **Hard occupancy + worker mesh polish** — physical credibility.

Keep watching primary; knobs optional.
