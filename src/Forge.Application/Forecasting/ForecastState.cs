namespace Forge.Application.Forecasting;

/// <summary>
/// The lifecycle state of a produced <see cref="DemandForecast"/> in the human-in-the-loop
/// review workflow (design "ML Forecasting and Human-in-the-Loop"; Req 22).
/// <para>
/// The state machine is <c>Pending → Accepted | Overridden | Accepted_By_Default</c>. A freshly
/// produced forecast starts <see cref="Pending"/> and awaits an operator decision. The
/// accept/override/deadline transitions themselves are the responsibility of
/// <c>SubmitForecastDecisionHandler</c> (task 24.6); this enum is the shared lifecycle vocabulary
/// that handler transitions between and that the <see cref="Forge.Contracts.Dtos.DemandForecastDto"/>
/// surfaces as its <c>State</c> string.
/// </para>
/// <para>
/// The string names here are the canonical wire values used by
/// <see cref="Forge.Contracts.Dtos.DemandForecastDto.State"/>:
/// <c>Pending | Accepted | Overridden | Accepted_By_Default</c> (Req 2.3, 23.4).
/// </para>
/// </summary>
public enum ForecastState
{
    /// <summary>Produced and awaiting an operator decision (the initial state). </summary>
    Pending = 0,

    /// <summary>An operator accepted the forecast; its values apply downstream (Req 22.2).</summary>
    Accepted,

    /// <summary>An operator overrode the forecast with validated values (Req 22.3, 22.5).</summary>
    Overridden,

    /// <summary>No operator responded within the deadline; the forecast auto-applied (Req 22.6).</summary>
    Accepted_By_Default,
}

/// <summary>
/// Canonical string names for <see cref="ForecastState"/> matching the values documented for
/// <see cref="Forge.Contracts.Dtos.DemandForecastDto.State"/>
/// (<c>Pending | Accepted | Overridden | Accepted_By_Default</c>).
/// <para>
/// The enum member names already match the wire values exactly, so the mapping is a stable
/// <c>ToString()</c>; this helper centralizes it so both the orchestrator (task 25.1) and the
/// forthcoming decision handler (task 24.6) project the state identically.
/// </para>
/// </summary>
public static class ForecastStateNames
{
    /// <summary>The canonical wire name for a <see cref="ForecastState"/> value.</summary>
    public static string ToWireName(this ForecastState state) => state.ToString();
}
