using Forge.Domain.Gels;
using Forge.Domain.Vessels;

namespace Forge.Application.Simulation;

internal static partial class TickStages
{
    /// <summary>
    /// Advance engine-owned starship phases: Away → Approaching → Docked/Unloading → Loading →
    /// Departing → Away. ~50% of visits arrive with inbound produce that must fully unload before
    /// outbound Loading. Full ships depart immediately; partially loaded ships depart after a
    /// minimum dwell. Dock berths are capped by <paramref name="openDockBays"/>. Visit cadence
    /// across a simulated day is driven by <paramref name="shipsPerDay"/>.
    /// </summary>
    public static void StarshipLifecycle(
        TickState state,
        DateTimeOffset now,
        TimeSpan delta,
        int openDockBays,
        double shipsPerDay = 4.0)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        EnsureRuntimes(state, now, shipsPerDay);

        var ships = Ordered(state.Starships, s => s.Id);
        var runtimes = state.StarshipRuntimes;

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

        TimeSpan awayDuration = AwayDurationFor(shipsPerDay, ships.Count);

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
                    var stagger = TimeSpan.FromSeconds(StableGuidFold(ship.Id.Value) % 90);
                    if (elapsed < awayDuration + stagger)
                    {
                        break;
                    }

                    int berth = FindFreeBerth(occupied, openDockBays);
                    if (berth < 0)
                    {
                        break;
                    }

                    EnterPhase(rt, StarshipPhases.Approaching, now);
                    rt.DockIndex = berth;
                    occupied.Add(berth);
                    BeginVisitCargo(ship, rt, now);
                    break;
                }

                case StarshipPhases.Approaching:
                {
                    if (rt.NeedsInboundMaterialization ||
                        (rt.PendingInboundLotCount > 0 && !rt.InboundCargoMaterialized))
                    {
                        rt.NeedsInboundMaterialization = true;
                        break;
                    }

                    if (elapsed < VisualSimulationConstants.ApproachDuration)
                    {
                        break;
                    }

                    if (rt.UnloadRemaining > 0 || rt.InboundLotIds.Count > 0)
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
                    int remaining = CountShipInboundRemaining(state, rt);
                    rt.UnloadRemaining = remaining;
                    _ = ship.SetLoadedQuantity(Math.Min(ship.CargoCapacity, remaining));

                    // Fully unloaded → clear inbound markers and open for colony outbound load.
                    if (remaining <= 0 &&
                        (rt.InboundCargoMaterialized || rt.PendingInboundLotCount == 0))
                    {
                        rt.InboundLotIds.Clear();
                        _ = ship.SetLoadedQuantity(0);
                        EnterPhase(rt, StarshipPhases.Loading, now);
                    }

                    break;
                }

                case StarshipPhases.Docked:
                case StarshipPhases.Loading:
                {
                    if (ship.RemainingCapacity <= 0)
                    {
                        EnterPhase(rt, StarshipPhases.Departing, now);
                        break;
                    }

                    // Idle / partial depart: after min dwell AND a readable cargo, leave even if
                    // not full so colony ships don't wait forever for a full hull.
                    if (rt.Phase == StarshipPhases.Loading &&
                        elapsed >= VisualSimulationConstants.MinLoadingDwell &&
                        ship.LoadedQuantity >= VisualSimulationConstants.MinDepartLoadedPallets)
                    {
                        EnterPhase(rt, StarshipPhases.Departing, now);
                        break;
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
                    rt.InboundLotIds.Clear();
                    rt.InboundCargoMaterialized = false;
                    rt.NeedsInboundMaterialization = false;
                    rt.PendingInboundLotCount = 0;
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

        // Outbound load only after inbound produce (if any) is fully off the ship.
        return rt.Phase == StarshipPhases.Loading &&
               rt.DockIndex >= 0 &&
               rt.UnloadRemaining <= 0;
    }

    private static void BeginVisitCargo(Starship ship, StarshipRuntime rt, DateTimeOffset now)
    {
        rt.InboundLotIds.Clear();
        rt.InboundCargoMaterialized = false;
        rt.NeedsInboundMaterialization = false;
        rt.PendingInboundLotCount = 0;
        rt.UnloadRemaining = 0;
        ship.ClearCargo();

        // Deterministic ~50% of visits bring produce (stable per ship + sim hour).
        uint fold = StableGuidFold(ship.Id.Value) ^ (uint)(now.UtcTicks / TimeSpan.TicksPerHour);
        bool bringsProduce = (fold % 100) < (uint)(VisualSimulationConstants.ShipInboundProduceChance * 100);
        if (!bringsProduce)
        {
            return;
        }

        int span = VisualSimulationConstants.MaxShipInboundPallets -
                   VisualSimulationConstants.MinShipInboundPallets + 1;
        int amount = VisualSimulationConstants.MinShipInboundPallets + (int)(fold % (uint)span);
        amount = Math.Min(amount, ship.CargoCapacity);

        rt.PendingInboundLotCount = amount;
        rt.UnloadRemaining = amount;
        rt.NeedsInboundMaterialization = true;
        _ = ship.SetLoadedQuantity(amount);
    }

    private static int CountShipInboundRemaining(TickState state, StarshipRuntime rt)
    {
        int remaining = 0;
        for (int i = 0; i < rt.InboundLotIds.Count; i++)
        {
            var id = rt.InboundLotIds[i];
            if (IsLotInList(state.InboundQueueLotIds, id) ||
                IsLotInList(state.InTransitLotIds, id))
            {
                remaining++;
            }
        }

        return remaining;
    }

    private static TimeSpan AwayDurationFor(double shipsPerDay, int shipCount)
    {
        int fleet = Math.Max(1, shipCount);
        double visits = Math.Max(0.25, shipsPerDay);
        // Space fleet visits across ~16 operating hours.
        double hoursBetween = (16.0 / visits) * fleet;
        hoursBetween = Math.Clamp(hoursBetween, 0.25, 12.0);
        return TimeSpan.FromHours(hoursBetween);
    }

    private static void EnsureRuntimes(TickState state, DateTimeOffset now, double shipsPerDay)
    {
        int i = 0;
        var away = AwayDurationFor(shipsPerDay, state.Starships.Count);
        foreach (var ship in Ordered(state.Starships, s => s.Id))
        {
            int idx = i;
            state.StarshipRuntimes.GetOrAdd(ship.Id, _ => new StarshipRuntime
            {
                Phase = StarshipPhases.Away,
                PhaseEnteredAt = now - away + TimeSpan.FromMinutes(idx * 15),
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
