# Forge

**Forge - Food Organization and Resource Guidance Engine**

A full Warehouse Management System (WMS), a warehouse simulation, and a visual management tool ("the game") for a futuristic food-gel distribution center.

<img width="2536" height="1341" alt="phase1" src="https://github.com/user-attachments/assets/d79dd542-5d56-419b-ab37-a6cd00b4a864" />


## Project Goal

Forge delivers three things that fit together as one solution:

1. **A full WMS** - a reusable, simulation-agnostic warehouse management core containing all warehouse business rules (FEFO selection, expiry/shelf-life decay, cold-chain temperature control, capacity limits, velocity/affinity slotting, labor-cost modeling, dock scheduling, spatial movement and contention, ML-backed demand forecasting with human-in-the-loop override). It is designed to be dropped into a real warehouse as a digital-twin/WMS foundation and runs fully headless.

2. **A simulation** - a pluggable input driver that feeds the WMS the inputs a real warehouse would otherwise receive: continuous inbound gel arrivals, evolving colony demand, and temperature readings, all advanced by a controllable (real-time or accelerated) clock. The simulation is what exercises the WMS; in a real deployment it would be swapped for a real-world driver with no change to the core.

3. **A visual management tool (the game)** - a live, top-down/isometric operations view in the style of a warehouse-management tycoon game. Operators watch agents move, spot congestion, tune live parameters, and review/override forecasts. It is a pure visualization and control surface with no business rules of its own, plus a read-only mobile cold-chain monitor.

## Theme

To make this more engaging, Forge is framed as a futuristic food-gel distribution center whose job is to fully manage the flow of resources to various off-world colonies. The warehouse sits as a constrained middle bottleneck between continuous inbound receiving and outbound shipping to colonies via starships.

## Approach

The project is built with spec-driven, AI-assisted development. After a thorough specification session, the work is broken into independent, verifiable tasks.

## Phase 1 Game Plan

The build order is deliberate, with the reusable value component first:

1. **WMS core** - build the reusable warehouse management engine (domain + application).
2. **Simulation** - build the input driver that generates arrivals, demand, and temperature readings.
3. **Visual management tool (the game)** - build the web operations view and the mobile monitor.

After the three layers are in place, the WMS is documented in depth: which typical warehouse problems it solves today, where the current implementation can be enhanced, and what is planned for future phases. See [`documents/WMS_solutions.md`](documents/WMS_solutions.md) for that mapping of common WMS problems to their solutions.

## Architecture at a Glance

![Forge architecture diagram](documents/architecture.png)

*The diagram above shows the project reference graph (solid arrows) and runtime transport (dashed arrows). Source: [`documents/architecture.mmd`](documents/architecture.mmd) (Mermaid); rendered copies in [`documents/architecture.png`](documents/architecture.png) and [`documents/architecture.svg`](documents/architecture.svg). Re-render with `npx @mermaid-js/mermaid-cli -i documents/architecture.mmd -o documents/architecture.png -b white -w 1600`.*


- **WMS_Core** (`Forge.Domain` + `Forge.Application`) - all business rules; depends only on abstractions; contains no input-generation logic.
- **Input_Driver** (`Forge.Simulation`) - supplies the core inputs; replaceable by a real-world driver.
- **Game** (`Forge.Game`) - Next.js web operations view + MAUI read-only cold-chain monitor; renders authoritative state only.
- Supporting projects: `Forge.Contracts` (shared DTOs/event schemas), `Forge.Infrastructure` (persistence, event bus, real-time transport, ML skeleton), and `Forge.Api` (headless REST + SignalR host).

## Running Forge

Forge Phase 1 runs the **headless engine**: the WMS Core driven by the accelerated Simulation input driver, exposing REST + SignalR. It uses **embedded Postgres** by default, so there is **no external database to install** — the first run downloads the Postgres binaries (~10 MB) once and reuses them thereafter.

> **New here? Read the [Operator Guide](documents/OPERATOR_GUIDE.md).** It is written like a video-game manual — how to "press start," what every on-screen control and metric means, and a plain-language glossary of the warehouse terms you will see.

### Prerequisites

- **.NET 10 SDK** (10.0.302 or later). Verify with `dotnet --version`.
- **Windows / macOS / Linux x64** — the embedded Postgres binaries auto-select for your OS/arch.
- First run needs **internet access** once to download the embedded Postgres binaries; subsequent runs are fully offline.
- Nothing else. No Postgres install, no Docker (Docker is only needed for the optional container-Postgres mode below).

The embedded database is persistent and lives under:
- Windows: `%LOCALAPPDATA%\Forge\pg`
- macOS/Linux: `~/.local/share/Forge/pg` (the platform LocalApplicationData folder)

Delete that folder to reset the database to a fresh seed.

### Run in VS Code (one-click debug)

1. Open the `warehouse` folder in VS Code (with the C# Dev Kit / C# extension installed).
2. Press **F5**, or pick **"Debug Api (headless, embedded Postgres)"** from the Run and Debug panel.
3. The `build-api` task compiles the Api, then the host starts. On first launch it downloads the embedded Postgres binaries, provisions the local instance, applies EF Core migrations, and seeds the warehouse (1000 gel types, temperature zones, 3-5 colonies, 1000+ gel lots). This can take a minute the first time.
4. When you see `Now listening on: http://localhost:5195`, the engine is live and the accelerated simulation is running — arrivals, colony demand, temperature readings, and per-tick rules are all advancing. VS Code will open the current snapshot endpoint automatically.

### Run from the command line (PowerShell / terminal)

From the repository root:

```powershell
# Build everything
dotnet build Forge.sln

# Run the headless engine (embedded Postgres, accelerated simulation)
dotnet run --project src/Forge.Api
```

The host prints its listening URL (default `http://localhost:5195`). Leave it running; the simulation advances continuously.

### See the engine working

With the host running, hit these endpoints (browser, curl, or any REST client):

- **Live snapshot** (inventory, zones, agents, starships, metrics, operator parameters):
  `GET http://localhost:5195/api/query/snapshot`
- **Operator parameters** (the six live knobs):
  `GET http://localhost:5195/api/operator-parameters`
- **Change a parameter** (e.g. workers on shift):
  `PUT http://localhost:5195/api/operator-parameters` with body `{ "key": "WorkersOnShift", "value": "8" }`
- **Create a colony order**: `POST http://localhost:5195/api/orders`
- **Real-time stream**: connect a SignalR client to `http://localhost:5195/hub/simulation`; on connect you receive a full `Snapshot`, then incremental updates (`LotExpired`, `TemperatureExcursion`, `BacklogChanged`, `OperatorParameterChanged`, and more).

Because the simulation runs on an accelerated clock, repeated snapshot calls a few seconds apart will show inventory, backlogs, and metrics changing as arrivals and colony demand flow through the warehouse.

### Run the tests

```powershell
dotnet test tests/Forge.Tests/Forge.Tests.csproj
```

The test suite runs entirely in-memory (no live database) and does not require the Game project.

### Optional: use an external / Docker Postgres instead of embedded

Embedded Postgres is the default. To point Forge at an external Postgres (e.g. a Docker container), edit `src/Forge.Api/appsettings.json`:

```json
"Forge": {
  "Embedded": false,
  "ConnectionString": "Host=localhost;Port=5432;Database=forge;Username=forge;Password=forge"
}
```

A `docker-compose.yml` for Postgres (and RabbitMQ, reserved for Phase 2) is provided at the repo root for this mode. If the configured Postgres is unreachable, the host fails startup with a descriptive error.

### The visual management tool (Game / web client)

The Next.js + React Three Fiber web operations client lives at `src/Forge.Game/web`. It is a pure client: it attaches to the running headless engine over REST + SignalR and renders authoritative state only (no business rules, no authoritative demand). It presents a pseudo-3D top-down "warehouse tycoon" view - moving worker/forklift agents, gel-lot cubes, colored temperature zones, docked starships, a live metrics HUD, and controls for the six operator parameters.

Start the engine first (see above), then from the repo root:

```powershell
cd src/Forge.Game/web
npm install        # first time only
npm run dev        # serves the client on http://localhost:3000
```

Then open `http://localhost:3000`. The client connects to the engine at `http://localhost:5195` by default; override with the `NEXT_PUBLIC_FORGE_API` environment variable. If the engine is not running yet, the client shows a "connecting" state and attaches automatically once the engine is up.

Once it is on screen, the [Operator Guide](documents/OPERATOR_GUIDE.md) walks through every control, metric, and warehouse term - game-manual style.

In VS Code the launch config includes a **Web client (Next.js dev)** entry and a **Forge: Engine + Web client** compound that starts both the engine and the web client together. Run npm install in `src/Forge.Game/web` once before the first launch.

The MAUI read-only cold-chain monitor is a later build phase.
