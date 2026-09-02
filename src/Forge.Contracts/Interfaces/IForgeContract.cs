namespace Forge.Contracts.Interfaces;

/// <summary>
/// Marker for the public Forge contract surface shared with the Game (Req 2.1, 2.3, 20.1).
/// The Game references only <c>Forge.Contracts</c> (DTOs, event schemas, operator-parameter
/// contract) plus the Api endpoints; it never references Domain, Application, or
/// Infrastructure types. This marker anchors the shared contract namespace and gives
/// clients a single, dependency-free type to key contract discovery/versioning against
/// without leaking any domain type across the boundary.
/// </summary>
public interface IForgeContract
{
    /// <summary>The version of the shared contract surface this client understands.</summary>
    string ContractVersion { get; }
}
