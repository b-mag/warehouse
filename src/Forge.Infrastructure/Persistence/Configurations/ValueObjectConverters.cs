using System.Text.Json;
using Forge.Domain.ColdChain;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Forge.Domain.Docks;
using Forge.Domain.Gels;
using Forge.Domain.Labor;
using Forge.Domain.Spatial;
using Forge.Domain.Vessels;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core value converters that map the domain's value objects (Req 3.2, 3.3, 6.1, 16.1, 18.1) to and
/// from a single scalar column. The affected aggregates expose these value objects only through
/// constructors that take them as parameters (no parameterless/scalar-only constructor and no public
/// setter). EF Core cannot bind a <em>complex property</em> or an <em>owned reference</em> to such a
/// constructor parameter, so — to keep the Domain persistence-ignorant and unchanged — the value objects
/// are stored as scalar (JSON) columns via these converters, which makes them ordinary mapped properties
/// EF can bind through the aggregate's existing constructor.
/// <para>
/// JSON is used (Postgres <c>jsonb</c>) so the multi-field value objects round-trip losslessly, and
/// value equality of the value objects makes change tracking behave correctly.
/// </para>
/// </summary>
internal static class ValueObjectConverters
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>
    /// Converts a <see cref="TemperatureRange"/> (inclusive Min/Max Celsius band, Req 6.1) to/from a
    /// JSON scalar column so it can be bound to a constructor parameter.
    /// </summary>
    public static readonly ValueConverter<TemperatureRange, string> TemperatureRange =
        new(
            range => JsonSerializer.Serialize(range, JsonOptions),
            json => JsonSerializer.Deserialize<TemperatureRange>(json, JsonOptions));

    /// <summary>
    /// Converts a <see cref="Cell"/> (integer grid coordinate, Req 18.1) to/from a JSON scalar column so
    /// it can be bound to a constructor parameter.
    /// </summary>
    public static readonly ValueConverter<Cell, string> Cell =
        new(
            cell => JsonSerializer.Serialize(cell, JsonOptions),
            json => JsonSerializer.Deserialize<Cell>(json, JsonOptions));

    /// <summary>
    /// Converts a <see cref="Formulation"/> (shared recipe: storage range, nominal shelf-life, flavors —
    /// Req 3.2, 3.3) to/from a JSON scalar column so it can be bound to a constructor parameter. The
    /// serialized form captures every attribute, preserving the value object's structural equality.
    /// </summary>
    public static readonly ValueConverter<Formulation, string> Formulation =
        new(
            formulation => Serialize(formulation),
            json => Deserialize(json));

    // Formulation exposes IReadOnlyList<string> via a constructor; a small DTO makes the JSON shape
    // explicit and round-trips it back through the domain constructor so the value object stays intact.
    private static string Serialize(Formulation formulation) =>
        JsonSerializer.Serialize(
            new FormulationDto(
                formulation.StorageRange,
                formulation.NominalShelfLife,
                [.. formulation.Flavors]),
            JsonOptions);

    private static Formulation Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<FormulationDto>(json, JsonOptions)
                  ?? throw new InvalidOperationException("A persisted formulation could not be deserialized.");

        return new Formulation(dto.StorageRange, dto.NominalShelfLife, dto.Flavors);
    }

    private sealed record FormulationDto(
        TemperatureRange StorageRange,
        TimeSpan NominalShelfLife,
        IReadOnlyList<string> Flavors);

    /// <summary>
    /// Converts a <see cref="Starship"/>'s <see cref="LoadingWindow"/> list (Req 13.1) to/from a JSON
    /// array column. The starship's only constructor takes the windows as a parameter, so a scalar JSON
    /// column is used rather than an owned child table. Each window round-trips through its validated
    /// factory to preserve the <c>End &gt; Start</c> invariant.
    /// </summary>
    public static readonly ValueConverter<List<LoadingWindow>, string> LoadingWindows =
        new(
            windows => JsonSerializer.Serialize(
                windows.Select(w => new WindowDto(w.Start, w.End)).ToList(), JsonOptions),
            json => DeserializeWindows(json));

    /// <summary>Value comparer for the <see cref="LoadingWindow"/> collection (order-sensitive, by value).</summary>
    public static readonly ValueComparer<List<LoadingWindow>> LoadingWindowsComparer =
        new(
            (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b)),
            list => list.Aggregate(0, (acc, w) => HashCode.Combine(acc, w)),
            list => list.ToList());

    /// <summary>
    /// Converts a <see cref="Worker"/>'s <see cref="WorkerShift"/> list (Req 15.1) to/from a JSON array
    /// column, bound to the worker's shift constructor parameter. Each shift round-trips through its
    /// validated factory to preserve the <c>End &gt; Start</c> invariant.
    /// </summary>
    public static readonly ValueConverter<IReadOnlyList<WorkerShift>, string> WorkerShifts =
        new(
            shifts => JsonSerializer.Serialize(
                shifts.Select(s => new WindowDto(s.Start, s.End)).ToList(), JsonOptions),
            json => DeserializeShifts(json));

    /// <summary>Value comparer for the <see cref="WorkerShift"/> collection (order-sensitive, by value).</summary>
    public static readonly ValueComparer<IReadOnlyList<WorkerShift>> WorkerShiftsComparer =
        new(
            (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b)),
            list => list.Aggregate(0, (acc, s) => HashCode.Combine(acc, s)),
            list => list.ToList());

    /// <summary>
    /// Converts a <see cref="ColonyOrder"/>'s <see cref="OrderLine"/> list (Req 12.1) to/from a JSON array
    /// column, bound to the order record's <c>Lines</c> constructor parameter.
    /// </summary>
    public static readonly ValueConverter<IReadOnlyList<OrderLine>, string> OrderLines =
        new(
            lines => JsonSerializer.Serialize(
                lines.Select(l => new OrderLineDto(l.GelType.Value, l.Quantity)).ToList(), JsonOptions),
            json => DeserializeOrderLines(json));

    /// <summary>Value comparer for the <see cref="OrderLine"/> collection (order-sensitive, by value).</summary>
    public static readonly ValueComparer<IReadOnlyList<OrderLine>> OrderLinesComparer =
        new(
            (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b)),
            list => list.Aggregate(0, (acc, l) => HashCode.Combine(acc, l)),
            list => list.ToList());

    /// <summary>
    /// Converts a <see cref="GelLot"/>'s <see cref="TemperatureReading"/> history (Req 6.2) to/from a JSON
    /// array column, bound through the lot's private <c>_history</c> backing field (there is no constructor
    /// parameter for it). Readings round-trip by value and stay in timestamp order.
    /// </summary>
    public static readonly ValueConverter<List<TemperatureReading>, string> TemperatureReadings =
        new(
            readings => JsonSerializer.Serialize(
                readings.Select(r => new ReadingDto(r.Celsius, r.At)).ToList(), JsonOptions),
            json => DeserializeReadings(json));

    /// <summary>Value comparer for the <see cref="TemperatureReading"/> history (order-sensitive, by value).</summary>
    public static readonly ValueComparer<List<TemperatureReading>> TemperatureReadingsComparer =
        new(
            (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b)),
            list => list.Aggregate(0, (acc, r) => HashCode.Combine(acc, r)),
            list => list.ToList());

    /// <summary>
    /// Converts a <see cref="Colony"/>'s <see cref="DemandProfile"/> value object (Req 12.1, 12.3, 12.6)
    /// to/from a JSON column, bound to the colony's profile constructor parameter. The base-rate map keys
    /// (strongly-typed <see cref="GelTypeId"/>) are serialized as their underlying Guid strings and the
    /// profile is rebuilt through its validated factory.
    /// </summary>
    public static readonly ValueConverter<DemandProfile, string> DemandProfile =
        new(
            profile => SerializeProfile(profile),
            json => DeserializeProfile(json));

    private static List<LoadingWindow> DeserializeWindows(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<WindowDto>>(json, JsonOptions) ?? [];
        var windows = new List<LoadingWindow>(dtos.Count);
        foreach (var dto in dtos)
        {
            var result = LoadingWindow.Create(dto.Start, dto.End);
            windows.Add(result.Value);
        }

        return windows;
    }

    private static IReadOnlyList<WorkerShift> DeserializeShifts(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<WindowDto>>(json, JsonOptions) ?? [];
        var shifts = new List<WorkerShift>(dtos.Count);
        foreach (var dto in dtos)
        {
            var result = WorkerShift.Create(dto.Start, dto.End);
            shifts.Add(result.Value);
        }

        return shifts;
    }

    private static IReadOnlyList<OrderLine> DeserializeOrderLines(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<OrderLineDto>>(json, JsonOptions) ?? [];
        return dtos.Select(d => new OrderLine(new GelTypeId(d.GelType), d.Quantity)).ToList();
    }

    private static List<TemperatureReading> DeserializeReadings(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<ReadingDto>>(json, JsonOptions) ?? [];
        return dtos.Select(d => new TemperatureReading(d.Celsius, d.At)).ToList();
    }

    private static string SerializeProfile(DemandProfile profile)
    {
        var dto = new ProfileDto(
            profile.BaseRatePerHour.ToDictionary(kv => kv.Key.Value.ToString(), kv => kv.Value),
            profile.Trends.Select(t => new TrendDto(t.StartsAt, t.Multiplier)).ToList());
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static DemandProfile DeserializeProfile(string json)
    {
        var dto = JsonSerializer.Deserialize<ProfileDto>(json, JsonOptions)
                  ?? new ProfileDto(new Dictionary<string, double>(), []);

        var rates = dto.BaseRatePerHour.ToDictionary(kv => new GelTypeId(Guid.Parse(kv.Key)), kv => kv.Value);
        var trends = dto.Trends.Select(t => TrendBoundary.Create(t.StartsAt, t.Multiplier).Value).ToList();

        var result = Forge.Domain.Colonies.DemandProfile.Create(rates, trends);
        return result.Value;
    }

    private sealed record WindowDto(DateTimeOffset Start, DateTimeOffset End);

    private sealed record OrderLineDto(Guid GelType, int Quantity);

    private sealed record ReadingDto(decimal Celsius, DateTimeOffset At);

    private sealed record TrendDto(DateTimeOffset StartsAt, double Multiplier);

    private sealed record ProfileDto(
        Dictionary<string, double> BaseRatePerHour,
        List<TrendDto> Trends);

    /// <summary>
    /// Converts a <see cref="DockBay"/>'s immutable <see cref="DockSchedule"/> (Req 17.1) to/from a JSON
    /// column, bound to the bay's schedule constructor parameter. The schedule's slots are serialized and
    /// the schedule is rebuilt through its constructor (which re-sorts them deterministically).
    /// </summary>
    public static readonly ValueConverter<DockSchedule, string> DockSchedule =
        new(
            schedule => JsonSerializer.Serialize(
                schedule.Slots.Select(s => new SlotDto(s.Start, s.End, s.Kind)).ToList(), JsonOptions),
            json => DeserializeSchedule(json));

    /// <summary>Value comparer for a <see cref="DockSchedule"/> (compares its ordered slots by value).</summary>
    public static readonly ValueComparer<DockSchedule> DockScheduleComparer =
        new(
            (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.Slots.SequenceEqual(b.Slots)),
            schedule => schedule.Slots.Aggregate(0, (acc, s) => HashCode.Combine(acc, s)),
            schedule => new DockSchedule(schedule.Slots));

    private static DockSchedule DeserializeSchedule(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<SlotDto>>(json, JsonOptions) ?? [];
        var slots = dtos.Select(d => new DockSlot(d.Start, d.End, d.Kind));
        return new DockSchedule(slots);
    }

    private sealed record SlotDto(DateTimeOffset Start, DateTimeOffset End, DockOperationKind Kind);
}
