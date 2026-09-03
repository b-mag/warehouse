# Forge - Operator Guide

*A field manual for running the warehouse. Think of it as the tutorial screen before you drop into the game.*

Welcome, Operator. You are in charge of a futuristic food-gel distribution center - the middle link in a supply chain that pulls product in from suppliers and ships it out to hungry off-world colonies. Your job is to keep gels flowing: received, stored cold, picked before they spoil, and loaded onto starships on time. The warehouse runs itself in real time; you watch it work and reach in to steer it.

You cannot "lose" in Phase 1 - there is no game-over. Instead you experiment: change a setting, watch what happens, and learn how a real warehouse behaves under pressure.

---

## 1. Starting a session ("Press Start")

You need two things running: the **engine** (the simulation brain) and the **web client** (your screen).

**The easy way (VS Code):**
1. Open the `warehouse` folder in VS Code.
2. First time only: open a terminal and run `cd src/Forge.Game/web` then `npm install`.
3. From the Run and Debug panel pick **"Forge: Engine + Web client"** and press the green play button (or F5).
4. The engine boots (the very first launch downloads a small embedded database - give it a minute), then your browser opens the operations view.

**The manual way (two terminals):**
- Terminal 1 - start the engine: `dotnet run --project src/Forge.Api`
- Terminal 2 - start the screen: `cd src/Forge.Game/web` then `npm run dev`
- Open `http://localhost:3000` in your browser.

When the top-left status reads **Connected**, you are live. If it says *Connecting* or *Engine offline*, the engine simply is not up yet - the screen will attach itself automatically the moment it is.

---

## 2. Reading the screen (the "HUD")

### The warehouse floor (center)
A top-down, slightly tilted 3D view of your facility - the "RollerCoaster Tycoon" angle.

- **Colored floor tiles = Temperature Zones.** Each zone is a climate-controlled storage area. Cooler zones are drawn in cooler blues; warmer zones in warmer tones. Every zone keeps its contents within a set temperature band.
- **Small cubes = Gel Lots.** Each cube is a batch of product sitting in a zone. Their color tells you their health:
  - **Pale / white** - healthy.
  - **Amber** - **at risk** (something is wrong; usually a temperature problem - see the glossary).
  - **Red** - **expired** (spoiled; it can no longer be shipped).
  - A number floats over each zone telling you how many lots it holds. (For readability the view draws a sample of cubes per zone, always keeping the problem lots visible - it is not drawing all thousand at once.)
- **Cone markers = Agents** (your workers / forklifts). They move around the floor doing jobs. A marker that lights up with a **red ring** is **held** - stuck waiting on a blocked path or an occupied dock/pick spot. Red rings = congestion = a bottleneck to investigate.
- **Large shapes at the front edge = Starships.** These dock to pick up outbound orders during their scheduled loading windows. A bar on each ship shows how full it is (loaded vs. capacity).

### Camera controls
- **Drag** to orbit the view.
- **Scroll** to zoom in/out.
- **Right-drag** to pan.
- The camera is locked to stay above the floor, so you cannot accidentally flip it sideways.

### Live Metrics (top-left panel)
Your dashboard gauges. This is where you "watch the bottleneck."

| Metric | What it means in plain terms |
|--------|------------------------------|
| **Receiving backlog** | How many inbound arrivals are waiting to be put away. A rising number means product is coming in faster than you can store it. |
| **Outbound backlog** | How many colony orders are waiting to be picked/shipped. A rising number means demand is outrunning your ability to fulfill. |
| **Inbound throughput** | How fast product is actually being received and stored (lots per unit of simulated time). |
| **Outbound throughput** | How fast orders are actually being fulfilled and shipped. |
| **Dock contention** | How many operations are queued waiting for a dock door. High = your docks are a chokepoint. |
| **Dock utilization** | What percentage of your dock capacity is in use. Near 100% means docks are maxed out. |

The art of warehouse management is spotting *which* of these is your limiting factor at any moment - and it moves around as you change settings.

### Event feed (bottom-left)
A running log of notable things the warehouse reports: blocked arrivals, temperature excursions, dock blocks, loading windows closing. Think of it as the incident ticker.

### Connection status (top-left)
**Connected / Connecting / Reconnecting / Engine offline.** If you restart the engine, the screen reconnects on its own.

---

## 3. Operator Controls (top-right panel) - your steering wheel

These are the six live knobs. Change one, then watch the Metrics and the floor react. Numeric controls apply when you press **Enter** or click away; the dropdown applies immediately. The engine confirms every change and the screen updates to match - so what you see is always the real state.

| Control | What it does | Try this |
|---------|--------------|----------|
| **Simulation speed** | The clock. `0` = paused, `1` = real-time, `>1` = fast-forward. | Crank it up to watch a full "day" play out in seconds; pause it to study a moment. |
| **Workers on shift** | How much labor you have. More workers = more picking/put-away happening at once. | Raise it step by step and watch outbound throughput climb - then **plateau**. The plateau tells you labor is no longer the bottleneck; something else (docks, storage) now is. |
| **Open dock bays** | How many dock doors are available for receiving and loading. Docks are shared between inbound and outbound. | If dock contention is high, open more bays and watch the queue drain. |
| **Inbound arrival rate** | How fast new product shows up at the door. | Flood the warehouse with a high rate and watch the receiving backlog balloon - a classic "too much, too fast" scenario. |
| **Colony demand multiplier** | How hungry the colonies are - scales all outbound orders. | Spike it to simulate a demand surge and see whether your inventory and labor can keep up before orders pile into the outbound backlog. |
| **Slotting strategy** | *How* the system decides where to store incoming product. **Velocity-affinity** puts fast-moving items in easier-to-reach spots (smart). **Naive first-available** just uses the first open slot (dumb). | Switch between them and compare throughput. This is a real lever warehouses obsess over. |

**The signature experiment:** start with a modest number of workers, then raise "Workers on shift" one notch at a time while watching "Outbound throughput." It rises, then stops rising even as you add more workers. That plateau *is* the bottleneck concept - and it teaches you that throwing bodies at a problem only helps until something else becomes the constraint.

---

## 4. Warehouse terms glossary (what the words mean)

Real warehouse vocabulary, in plain language:

- **Gel Lot** - a single batch of one product, received as one unit. Has a quantity, an expiry date, and a home zone.
- **Gel Type / Formulation** - the "recipe" for a product: its required storage temperature, shelf-life, and flavor. There are 1000 distinct types seeded in the world.
- **Temperature Zone** - a storage area that keeps a specific temperature range. Product must live in a zone that matches its needs.
- **Temperature Excursion** - the cold chain broke: a lot's temperature went outside its allowed range. This flags the lot **at risk** (amber). Real-world, this is how food spoils in transit.
- **Shelf-Life / Expiry** - how long a lot stays good. Time passes in the sim, shelf-life ticks down, and at zero the lot **expires** (red) and can no longer be shipped. Waste.
- **FEFO (First-Expired-First-Out)** - the rule for choosing which lots to ship: always send the ones that expire soonest first, so product gets used before it spoils. (This is why good operators care about expiry, not just quantity.)
- **Slotting** - deciding where to store incoming product. Good slotting puts fast-movers where they are quick to reach; bad slotting wastes worker travel time.
- **Put-Away** - the job of taking a just-received lot and storing it in its zone.
- **Pick** - the job of pulling lots from storage to fulfill an order.
- **Dock Bay** - a door where trucks/ships load and unload. A shared, limited resource - inbound and outbound both compete for it.
- **Starship** - the outbound vehicle. It has a cargo capacity and scheduled **loading windows**; you can only load it during its window, and only up to capacity.
- **Colony** - a customer world. Each colony consumes product over time following its own demand pattern, which shifts as trends change.
- **Colony Order** - a request from a colony for a quantity of gels by a deadline. Fulfilling it is your outbound work.
- **Backlog** - work that is queued and waiting. Receiving backlog = product waiting to be stored; outbound backlog = orders waiting to be shipped. Backlogs are your early-warning signal.
- **Throughput** - the rate at which work actually gets done. The number that matters most.
- **Bottleneck** - whatever is currently limiting your throughput (labor, docks, storage, or inventory). It moves as conditions change. Finding and relieving it is the whole game.
- **Agent** - the on-screen worker/forklift moving product around. When it is stuck waiting (red ring), that is **contention**.
- **Contention** - two jobs wanting the same resource (a path, a dock, a pick spot) at once. One waits. Too much contention throttles everything.

---

## 5. Suggested first play session

1. **Watch (2 min).** Leave everything default. Get a feel for the floor and the metrics.
2. **Speed up.** Set Simulation speed to `5`. Watch inventory and backlogs move faster.
3. **Find a bottleneck.** Push Colony demand multiplier to `3`. Watch outbound backlog grow. Now raise Workers on shift and see if you can drain it - or whether docks become the new limit.
4. **Compare strategies.** Flip Slotting strategy between velocity-affinity and naive first-available and compare outbound throughput after things settle.
5. **Break it on purpose.** Crank Inbound arrival rate way up and watch the receiving backlog balloon - then figure out which knobs bring it back under control.

There is no wrong way to poke it. The point is to build intuition for how a warehouse behaves as a living system.

---

## 6. Troubleshooting

- **Status stuck on "Connecting / Engine offline"** - the engine is not running (or not on port 5195). Start it (`dotnet run --project src/Forge.Api`) and the screen reconnects automatically.
- **First launch is slow** - the embedded database downloads once (~10 MB) and initializes, then future launches are fast. This is normal.
- **The 3D view shows an error message** - the engine and controls are still live; only the WebGL scene hiccuped. Refresh the page.
- **Nothing is moving** - make sure Simulation speed is above `0` (0 = paused).
- **Want a fresh world** - stop the engine and delete the embedded database folder (`%LOCALAPPDATA%\Forge\pg` on Windows), then start again to re-seed.

Happy operating. The warehouse is yours.