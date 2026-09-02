namespace Forge.Domain.Colonies;

using Forge.Domain.Common;

/// <summary>
/// A colony the warehouse supplies (Req 12.1). Pure data: a colony is just an identity plus its
/// <see cref="DemandProfile"/>. It carries no generation logic — authoritative colony-demand
/// generation lives in <c>Forge.Simulation.ColonyDemandSimulator</c> (task 27), which reads the
/// profile to evolve consumption and issue orders (Req 1.8).
/// </summary>
public sealed class Colony
{
    /// <summary>Create a colony from its identity and validated demand profile.</summary>
    public Colony(ColonyId id, DemandProfile profile)
    {
        Id = id;
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>The colony's strongly-typed identifier.</summary>
    public ColonyId Id { get; }

    /// <summary>The colony's demand shape (base rates + trend boundaries).</summary>
    public DemandProfile Profile { get; }
}
