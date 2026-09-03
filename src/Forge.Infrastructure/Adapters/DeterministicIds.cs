using System.Security.Cryptography;
using System.Text;

namespace Forge.Infrastructure.Adapters;

/// <summary>
/// Derives stable, deterministic <see cref="Guid"/>s from a seed, a namespace tag, and an ordinal —
/// the same MD5-of-bytes technique the seeder (<c>WarehouseSeeder</c>) and <c>GelTypeGenerator</c> use
/// (task 33.3). This lets the composition-root adapters (synthesized agents, starships, dock bays)
/// mint ids reproducibly instead of touching <see cref="Guid.NewGuid"/>, so an identical seed
/// reproduces an identical demo world. No security is implied; it is purely a reproducible 128-bit
/// hash.
/// </summary>
internal static class DeterministicIds
{
    /// <summary>Derive a stable <see cref="Guid"/> from <paramref name="seed"/> + <paramref name="tag"/> + <paramref name="ordinal"/>.</summary>
    internal static Guid Derive(int seed, string tag, int ordinal)
    {
        var payload = Encoding.UTF8.GetBytes($"forge-{tag}::{seed}::{ordinal}");
        var digest = MD5.HashData(payload); // 16 bytes -> exactly one Guid.
        return new Guid(digest);
    }
}
