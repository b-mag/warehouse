# Forge Web Operations Client

A Next.js (App Router + TypeScript) web client for the **Forge** warehouse engine. It is a
_RollerCoaster-Tycoon-style_ operations view: a pseudo-3D top-down / slight-isometric scene
where you watch product cubes and worker/forklift agents move through the warehouse in real
time, plus a management panel where you tune live parameters and watch the system react.

This app corresponds to spec tasks **37.1–37.4** of `nutrient-forge`
(Req 2.1, 24.1–24.6, 24.9, 24.10).

## Running

```bash
npm install      # install dependencies
npm run dev      # start the dev server on http://localhost:3000
npm run build    # production build
npm run start    # serve the production build
npm run lint     # lint
```

### Engine connection

The client talks to the **headless Forge engine**, which is a separate .NET process. By
default it targets `http://localhost:5195`:

- REST base: `http://localhost:5195/api/...`
- SignalR hub: `http://localhost:5195/hub/simulation`

Configure a different engine location with the `NEXT_PUBLIC_FORGE_API` environment variable
(the REST base and hub URL are both derived from it):

```bash
# .env.local
NEXT_PUBLIC_FORGE_API=http://localhost:5195
```

The app builds and runs **without** the engine. When the engine is offline it shows a
"connecting to engine…" / "engine offline" state instead of crashing, and reconnects
automatically when the engine comes back.

## How state flows

The client keeps a single authoritative render-state store
(`src/lib/store.ts`), owned by `ForgeProvider` (`src/lib/ForgeProvider.tsx`):

1. **Seed (REST + SignalR `Snapshot`).** On mount the provider fetches
   `GET /api/query/snapshot` as a cold seed, then opens the SignalR connection. On connect
   the hub sends a full `SimulationSnapshotDto` as a message named **`Snapshot`** (Req 23.3),
   which replaces the store's snapshot.
2. **Incremental events.** The provider subscribes to these exact engine message names and
   files each authoritative fact into the last-known snapshot:
   `LotExpired`, `TemperatureExcursion`, `BlockedArrival`, `BlockedPlacement`,
   `UnroutableTask`, `DockBlocked`, `LoadingWindowClosed`, `BacklogChanged`,
   `OperatorParameterChanged`.
3. **Render.** The scene (`src/components/scene`) and HUD (`src/components/hud`) render
   purely from that store.

Agents interpolate smoothly along their authoritative `pathCells` at `cellsPerSecond` for
readability, but **snap** to the authoritative `(x, y)` on every new snapshot and never
invent a destination. An agent that isn't advancing shows a contention/hold indicator.

## Pure-renderer boundary (Req 24.9, 24.10, 2.4)

**This client is a pure renderer + operator-control surface. It computes no business rules
and generates no authoritative colony demand.**

- It renders **only** authoritative DTO state received from the engine (snapshot + events).
- It computes **none** of the business rules — no FEFO/expiry, no temperature-excursion or
  at-risk determination, no capacity/slotting, no labor or throughput math. Those all live in
  the WMS core; the client only displays the flags/values the engine reports.
- It generates **no** authoritative colony demand. Authoritative `Colony_Order` generation
  lives in the Simulation driver's `ColonyDemandSimulator`, not here.
- Every operator action goes **through the Api** (`PUT /api/operator-parameters`,
  `POST /api/orders`); the client triggers the effect and then converges on the engine's
  authoritative echo (e.g. the `OperatorParameterChanged` message).

If a value isn't in a Contracts DTO, this client does not fabricate it.

## Project structure

```
web/
  src/
    app/
      layout.tsx          # root layout + metadata
      page.tsx            # wraps the dashboard in ForgeProvider
    components/
      Dashboard.tsx       # view + HUD composition
      scene/              # React Three Fiber pseudo-3D scene
        OperationsView.tsx  # canvas, camera, lights, composition
        Zones.tsx           # temperature zones as colored floor regions
        Lots.tsx            # gel lots as cubes (color/stack by state/quantity)
        Agents.tsx          # workers/forklifts with path interpolation + hold
        Starships.tsx       # larger docked shapes with loaded/capacity
      hud/                # HUD overlays (operator controls, metrics, events)
        OperatorPanel.tsx   # six operator parameters, PUT through the Api
        MetricsHud.tsx      # live backlog/throughput/dock metrics
        ConnectionStatus.tsx
        EventLog.tsx
    lib/
      contracts.ts        # TypeScript mirrors of the Forge.Contracts DTOs
      config.ts           # NEXT_PUBLIC_FORGE_API base + route helpers
      api.ts              # REST client (snapshot, operator params, orders)
      store.ts            # authoritative render-state reducer
      ForgeProvider.tsx   # SignalR connection + store provider
      layout.ts           # deterministic scene layout + color helpers
```

## Stack

- Next.js (App Router, TypeScript, ESLint, Tailwind CSS)
- [`three`](https://threejs.org/), [`@react-three/fiber`](https://r3f.docs.pmnd.rs/),
  [`@react-three/drei`](https://github.com/pmndrs/drei) for the pseudo-3D scene
- [`@microsoft/signalr`](https://learn.microsoft.com/aspnet/core/signalr/javascript-client)
  for the real-time channel
