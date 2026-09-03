using Forge.Domain.Vessels;

namespace Forge.Application.Simulation;

internal static partial class TickStages
{
    /// <summary>
    /// Advance engine-owned starship phases: Away → Approaching → Docked/Unloading → Loading →
    /// Departing → Away. When a ship hits capacity it departs immediately (visual fly-out), clears
    /// cargo, and later approaches again. Dock berths are capped by <paramref name="openDockBays"/>.
    /// </summary>
    public static void StarshipLifecycle(
        TickState state,
        DateTimeOffset now,
        TimeSpan delta,
        int openDockBays)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        EnsureRuntimes(state, now);

        var ships = Ordered(state.Starships, s => s.Id);
        var runtimes = state.StarshipRuntimes;

        // Occupied berths (Docked / Unloading / Loading).
        var occupied = new HashSet<int>();
        foreach (var ship in ships)
        {
            if (!runtimes.TryGetValue(ship.Id, out var rt))
            {
                continue;
            }

            if (rt.DockIndex >= 0 &&
                rt.Phase is StarshipPhases.Docked or StarshipPhases.Unloading or StarshipPhases.Loading)
            {
                occupied.Add(rt.DockIndex);
            }
        }

        foreach (var ship in ships)
        {
            if (!runtimes.TryGetValue(ship.Id, out var rt))
            {
                continue;
            }

            var elapsed = now - rt.PhaseEnteredAt;

            switch (rt.Phase)
            {
                case StarshipPhases.Away:
                {
                    // Stagger re-entry slightly by ship id so berths don't all fill on the same tick.
                    var stagger = TimeSpan.FromSeconds(StableGuidFold(ship.Id.Value) % 45);
                    if (elapsed < VisualSimulationConstants.AwayDuration + stagger)
                    {
                        break;
                    }

                    int berth = FindFreeBerth(occupied, openDockBays);
                    if (berth < 0)
                    {
                        break;
                    }

                    // ~1/3 of arrivals bring inbound cargo to unload (deterministic per ship+cycle).
                    uint fold = StableGuidFold(ship.Id.Value) ^ (uint)(now.UtcTicks / TimeSpan.TicksPerMinute);
                    int inbound = (fold % 3) == 0
                        ? (int)(1 + (fold % (uint)Math.Max(1, VisualSimulationConstants.StarshipCargoCapacityPallets / 4)))
                        : 0;

                    EnterPhase(rt, StarshipPhases.Approaching, now);
                    rt.DockIndex = berth;
                    rt.UnloadRemaining = inbound;
                    occupied.Add(berth);
                    break;
                }

                case StarshipPhases.Approaching:
                {
                    if (elapsed < VisualSimulationConstants.ApproachDuration)
                    {
                        break;
                    }

                    if (rt.UnloadRemaining > 0)
                    {
                        EnterPhase(rt, StarshipPhases.Unloading, now);
                    }
                    else
                    {
                        EnterPhase(rt, StarshipPhases.Loading, now);
                    }

                    break;
                }

                case StarshipPhases.Unloading:
                {
                    // Phase-1 interim: drain inbound cargo in the lifecycle until worker-unload tasks
                    // fully own this path. Keeps the phase visible without blocking the load story.
                    if (rt.UnloadRemaining > 0)
                    {
                        rt.UnloadRemaining = Math.Max(
                            0,
                            rt.UnloadRemaining - VisualSimulationConstants.MaxPalletsLoadedPerTick);
                    }

                    if (rt.UnloadRemaining <= 0)
                    {
                        EnterPhase(rt, StarshipPhases.Loading, now);
                    }

                    break;
                }

                case StarshipPhases.Docked:
                case StarshipPhases.Loading:
                {
                    // Full ship always departs — this is the fix for stuck 500/500 Docked.
                    if (ship.RemainingCapacity <= 0)
                    {
                        EnterPhase(rt, StarshipPhases.Departing, now);
                        break;
                    }

                    // Idle / no more demand after a minimum dwell → depart partially loaded.
                    if (rt.Phase == StarshipPhases.Loading &&
                        elapsed >= VisualSimulationConstants.MinLoadingDwell &&
                        ship.LoadedQuantity > 0 &&
                        ship.RemainingCapacity == ship.CargoCapacity)
                    {
                        // LoadedQuantity > 0 && Remaining == Capacity is impossible; keep for clarity.
                    }

                    if (rt.Phase == StarshipPhases.Docked)
                    {
                        EnterPhase(rt, StarshipPhases.Loading, now);
                    }

                    break;
                }

                case StarshipPhases.Departing:
                {
                    if (elapsed < VisualSimulationConstants.DepartDuration)
                    {
                        break;
                    }

                    ship.ClearCargo();
                    rt.DockIndex = -1;
                    rt.UnloadRemaining = 0;
                    EnterPhase(rt, StarshipPhases.Away, now);
                    break;
                }
            }
        }
    }

    /// <summary>True when the ship is berthed and allowed to take outbound cargo this tick.</summary>
    public static bool CanLoadStarship(TickState state, Starship ship)
    {
        if (!state.StarshipRuntimes.TryGetValue(ship.Id, out var rt))
        {
            return false;
        }

        return rt.Phase == StarshipPhases.Loading && rt.DockIndex >= 0;
    }

    private static void EnsureRuntimes(TickState state, DateTimeOffset now)
    {
        int i = 0;
        foreach (var ship in Ordered(state.Starships, s => s.Id))
        {
            state.StarshipRuntimes.GetOrAdd(ship.Id, _ => new StarshipRuntime
            {
                Phase = StarshipPhases.Away,
                // Stagger initial Away so ships approach one after another at startup.
                PhaseEnteredAt = now - VisualSimulationConstants.AwayDuration + TimeSpan.FromSeconds(i * 20),
                DockIndex = -1,
                UnloadRemaining = 0,
            });
            i++;
        }
    }

    private static void EnterPhase(StarshipRuntime rt, string phase, DateTimeOffset now)
    {
        rt.Phase = phase;
        rt.PhaseEnteredAt = now;
    }

    private static int FindFreeBerth(HashSet<int> occupied, int openDockBays)
    {
        int bays = Math.Max(0, openDockBays);
        for (var i = 0; i < bays; i++)
        {
            if (!occupied.Contains(i))
            {
                return i;
            }
        }

        return -1;
    }
}
