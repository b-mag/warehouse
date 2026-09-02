namespace Forge.Domain.Common;

/// <summary>
/// Discriminant for the domain's typed error set (Req 5.5, 6.4, 7.2, 7.4, 7.6, 7.7, 15.8, 16.3, 17.6, 20.8).
/// <para>
/// Every rejectable domain/application operation returns a <see cref="Result"/> or
/// <see cref="Result{T}"/> carrying one of these kinds rather than throwing, so callers
/// and the Api can map rejections to HTTP responses / events consistently. The kinds map
/// directly onto the rows of the design's Error Handling table.
/// </para>
/// </summary>
public enum ErrorKind
{
    /// <summary>Fallback / generic validation failure not covered by a more specific kind.</summary>
    Validation = 0,

    /// <summary>Invalid fulfillment request: qty &lt; 1, non-integer, or unknown gel type (Req 5.5).</summary>
    InvalidRequest,

    /// <summary>Temperature recorded for a lot with no assigned zone (Req 6.4).</summary>
    NoAssignedZone,

    /// <summary>Put-away / load would exceed capacity or requested a non-positive quantity (Req 7.2, 7.4, 7.6).</summary>
    CapacityExceeded,

    /// <summary>A negative capacity was configured for a zone or starship (Req 7.7).</summary>
    InvalidCapacity,

    /// <summary>A configured value was invalid, e.g. negative worker rate or negative task duration (Req 15.8).</summary>
    InvalidValue,

    /// <summary>A single-occupancy resource / dock slot is not available for acquisition (Req 17.x).</summary>
    SlotUnavailable,

    /// <summary>No compatible zone with capacity exists; the lot cannot be slotted (Req 16.3).</summary>
    Unslottable,

    /// <summary>No traversable path exists between origin and destination (Req 18.6).</summary>
    Unroutable,

    /// <summary>An operation was attempted outside all valid loading windows (Req 13.3).</summary>
    WindowClosed,
}

/// <summary>
/// A typed, discriminated domain error (Req 5.5 and the design's Error Handling table).
/// <para>
/// Modeled as an <see cref="ErrorKind"/> discriminant plus a human-readable
/// <see cref="Message"/> and an optional structured <see cref="Detail"/> bag. The structured
/// detail lets a rejection carry machine-readable context (e.g. requested vs. remaining
/// capacity for a <see cref="ErrorKind.CapacityExceeded"/>, the offending parameter name for
/// a validation failure) without forcing a bespoke error subclass per condition. This keeps
/// the error set small, consistent, and easy to map at the Api boundary while still conveying
/// everything the design's table requires each rejection to report.
/// </para>
/// </summary>
public sealed record DomainError(
    ErrorKind Kind,
    string Message,
    IReadOnlyDictionary<string, object?>? Detail = null)
{
    /// <summary>Attach or extend structured detail, returning a new error (records are immutable).</summary>
    public DomainError WithDetail(string key, object? value)
    {
        var next = Detail is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(Detail);
        next[key] = value;
        return this with { Detail = next };
    }

    public override string ToString() => $"{Kind}: {Message}";

    // ---- Factories, one per condition in the design's Error Handling table ----

    /// <summary>Invalid fulfillment request (Req 5.5).</summary>
    public static DomainError InvalidRequest(string message) =>
        new(ErrorKind.InvalidRequest, message);

    /// <summary>Temperature recorded for a zone-less lot (Req 6.4).</summary>
    public static DomainError NoAssignedZone(string message) =>
        new(ErrorKind.NoAssignedZone, message);

    /// <summary>
    /// Put-away / load exceeds capacity or non-positive quantity (Req 7.2, 7.4, 7.6).
    /// Carries the requested quantity and remaining capacity as required by the table.
    /// </summary>
    public static DomainError CapacityExceeded(string message, int requested, int remainingCapacity) =>
        new(ErrorKind.CapacityExceeded, message, new Dictionary<string, object?>
        {
            ["requested"] = requested,
            ["remainingCapacity"] = remainingCapacity,
        });

    /// <summary>Negative capacity configured (Req 7.7).</summary>
    public static DomainError InvalidCapacity(string message) =>
        new(ErrorKind.InvalidCapacity, message);

    /// <summary>Invalid configured value, e.g. negative rate/duration (Req 15.8).</summary>
    public static DomainError InvalidValue(string message) =>
        new(ErrorKind.InvalidValue, message);

    /// <summary>Single-occupancy resource / dock slot unavailable (Req 17.6).</summary>
    public static DomainError SlotUnavailable(string message) =>
        new(ErrorKind.SlotUnavailable, message);

    /// <summary>No compatible zone with capacity (Req 16.3).</summary>
    public static DomainError Unslottable(string message) =>
        new(ErrorKind.Unslottable, message);

    /// <summary>No traversable path to destination (Req 18.6).</summary>
    public static DomainError Unroutable(string message) =>
        new(ErrorKind.Unroutable, message);

    /// <summary>Operation attempted outside all valid loading windows (Req 13.3).</summary>
    public static DomainError WindowClosed(string message) =>
        new(ErrorKind.WindowClosed, message);

    /// <summary>Generic validation failure. Names the offending field/parameter (Req 20.8).</summary>
    public static DomainError Validation(string message, string? parameter = null)
    {
        var error = new DomainError(ErrorKind.Validation, message);
        return parameter is null ? error : error.WithDetail("parameter", parameter);
    }
}
