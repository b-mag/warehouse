using Forge.Domain.Common;

namespace Forge.Application.Simulation;

internal static partial class TickStages
{
    /// <summary>
    /// Advance the inbound train: Away → Approaching → Unloading → Departing → Away.
    /// Cargo is requested as soon as Approaching starts so the consist arrives WITH pallets.
    /// Visits are spaced by trains-per-day from the current sim clock (not a one-shot boot visit).
    /// </summary>
    public static void TrainLifecycle(
        TickState state,
        DateTimeOffset now,
        TimeSpan delta,
        double trainsPerDay)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        var train = state.Train;
        var elapsed = now - train.PhaseEnteredAt;

        switch (train.Phase)
        {
            case TrainPhases.Away:
            {
                if (trainsPerDay <= 0)
                {
                    break;
                }

                // Space visits evenly across a sim day from "now" (first visit arrives immediately).
                if (train.NextArrivalAt is null)
                {
                    train.NextArrivalAt = now;
                }

                if (now < train.NextArrivalAt.Value)
                {
                    break;
                }

                double hoursBetween = 24.0 / Math.Max(0.25, trainsPerDay);
                train.NextArrivalAt = now + TimeSpan.FromHours(hoursBetween);
                train.CargoMaterialized = false;
                train.NeedsCargoMaterialization = true; // arrive with cargo
                train.LotIds.Clear();
                train.CarCount = Math.Max(
                    1,
                    (int)Math.Ceiling(
                        VisualSimulationConstants.TrainLotsPerVisit /
                        (double)VisualSimulationConstants.TrainCarCapacityPallets));
                EnterTrainPhase(train, TrainPhases.Approaching, now);
                break;
            }

            case TrainPhases.Approaching:
            {
                if (!train.CargoMaterialized)
                {
                    train.NeedsCargoMaterialization = true;
                    break;
                }

                if (elapsed < VisualSimulationConstants.TrainApproachDuration)
                {
                    break;
                }

                EnterTrainPhase(train, TrainPhases.Unloading, now);
                break;
            }

            case TrainPhases.Unloading:
            {
                bool anyRemaining = false;
                for (int i = 0; i < train.LotIds.Count; i++)
                {
                    if (IsLotInList(state.InboundQueueLotIds, train.LotIds[i]))
                    {
                        anyRemaining = true;
                        break;
                    }
                }

                // Stay visible while workers strip the cars; require a minimum dwell.
                if (!anyRemaining &&
                    train.CargoMaterialized &&
                    elapsed >= VisualSimulationConstants.TrainMinUnloadDwell)
                {
                    // Keep lot ids for the last frames of Unloading; clear on Departing.
                    EnterTrainPhase(train, TrainPhases.Departing, now);
                }

                break;
            }

            case TrainPhases.Departing:
            {
                if (elapsed < VisualSimulationConstants.TrainDepartDuration)
                {
                    break;
                }

                train.LotIds.Clear();
                train.CarCount = 0;
                train.CargoMaterialized = false;
                train.NeedsCargoMaterialization = false;
                EnterTrainPhase(train, TrainPhases.Away, now);
                break;
            }
        }
    }

    private static void EnterTrainPhase(TrainRuntime train, string phase, DateTimeOffset now)
    {
        train.Phase = phase;
        train.PhaseEnteredAt = now;
    }
}
