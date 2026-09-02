namespace Forge.Contracts.OperatorParameters;

/// <summary>
/// The allowable slotting-strategy identifiers (Req 20.7). Mirrors
/// <c>ISlottingStrategy.Key</c> in the Application layer so clients and the
/// core reference a single source of truth.
/// </summary>
public static class SlottingStrategyKey
{
    public const string VelocityAffinity = "velocity-affinity";
    public const string NaiveFirstAvailable = "naive-first-available";

    /// <summary>All valid slotting-strategy keys, in stable order.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        VelocityAffinity,
        NaiveFirstAvailable,
    };

    /// <summary>True when <paramref name="key"/> is a recognized slotting strategy.</summary>
    public static bool IsValid(string? key) =>
        key is VelocityAffinity or NaiveFirstAvailable;
}
