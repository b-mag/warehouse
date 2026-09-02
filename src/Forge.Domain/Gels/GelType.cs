using Forge.Domain.Common;

namespace Forge.Domain.Gels;

/// <summary>
/// A family of gel that shares a single <see cref="Formulation"/> (Req 3.2). Every produced
/// <see cref="GelLot"/> of this type inherits the formulation's storage requirements and nominal
/// shelf-life.
/// <para>
/// <see cref="Velocity"/> is the turnover rate used by velocity-affinity slotting: higher-velocity
/// gels are preferred for more accessible, compatible zones so their expected future travel time
/// is minimized (Req 16). It is carried on the type (not the lot) because turnover is a property
/// of the product, not an individual batch.
/// </para>
/// </summary>
public sealed class GelType
{
    /// <summary>
    /// Create a gel type. <paramref name="velocity"/> must be finite and non-negative — a turnover
    /// rate is a rate, never negative and never NaN/∞.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="formulation"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="velocity"/> is negative, NaN, or infinite.</exception>
    public GelType(GelTypeId id, Formulation formulation, double velocity)
    {
        ArgumentNullException.ThrowIfNull(formulation);

        if (double.IsNaN(velocity) || double.IsInfinity(velocity) || velocity < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(velocity),
                velocity,
                "Velocity must be a finite, non-negative turnover rate.");
        }

        Id = id;
        Formulation = formulation;
        Velocity = velocity;
    }

    /// <summary>The strongly-typed identity of this gel type (Req 3.1).</summary>
    public GelTypeId Id { get; }

    /// <summary>The shared recipe/spec: storage range, nominal shelf-life, flavors (Req 3.2, 3.3).</summary>
    public Formulation Formulation { get; }

    /// <summary>Turnover rate used by slotting to place fast movers more accessibly (Req 16).</summary>
    public double Velocity { get; }
}
