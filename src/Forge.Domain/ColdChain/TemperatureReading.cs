namespace Forge.Domain.ColdChain;

/// <summary>
/// A single recorded temperature value with the timestamp at which it was observed (Req 6.2).
/// <para>
/// Modeled as an immutable <c>sealed record</c> so readings have value equality and can be
/// accumulated into a gel lot's temperature history in timestamp order. This type is OWNED by
/// the cold-chain subsystem (task 7.1) and is referenced by the Gels subsystem (a
/// <c>GelLot</c> keeps an <c>IReadOnlyList&lt;TemperatureReading&gt;</c> history). It is defined
/// here exactly as the design's domain model prescribes.
/// </para>
/// <para>
/// The append-to-history-in-timestamp-order behavior (Req 6.2) and excursion handling live in
/// the <c>RecordTemperatureReading</c> handler (task 24.3) operating on <c>GelLot</c>; this type
/// is purely the recorded value + timestamp.
/// </para>
/// </summary>
/// <param name="Celsius">The recorded temperature value, in degrees Celsius.</param>
/// <param name="At">The timestamp at which the reading was taken.</param>
public sealed record TemperatureReading(decimal Celsius, DateTimeOffset At);
