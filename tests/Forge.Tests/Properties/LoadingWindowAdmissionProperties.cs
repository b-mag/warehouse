using CsCheck;
using Forge.Application.Loading;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Domain.Vessels;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 5: Starship loading-window admission
//
// Validates: Requirements 13.2, 13.3
//
// For any starship with one or more loading windows and any current simulated time, a load SHALL be
// permitted only when the current time falls within [window.Start, window.End] of some window; a load
// requested outside every window SHALL be rejected with the loaded quantity unchanged.
public sealed class LoadingWindowAdmissionProperties
{
    private const int Iterations = 100;

    // A fixed epoch; window bounds and "now" are generated as whole-second offsets from it so the
    // inclusive-bounds admission check is exercised exactly, including moments on a window boundary.
    private static readonly DateTimeOffset Epoch = new(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly GelTypeId TargetType = new(new Guid("33333333-3333-3333-3333-333333333333"));
    private static readonly TimeSpan NominalShelfLife = TimeSpan.FromDays(30);

    private static readonly StarshipLoadingService Service = new();

    // A window blueprint: a start offset (seconds from Epoch) and a strictly-positive duration so the
    // domain factory (which requires End > Start) always accepts it.
    private readonly record struct WindowSpec(int StartOffsetSeconds, int DurationSeconds);

    private static readonly Gen<WindowSpec> GenWindowSpec =
        from start in Gen.Int[0, 10_000]
        from duration in Gen.Int[1, 2_000]
        select new WindowSpec(start, duration);

    // 1..5 windows per starship (Req 13.1 requires at least one).
    private static readonly Gen<WindowSpec[]> GenWindowSpecs = GenWindowSpec.Array[1, 5];

    // "now" offset spans well before, within, on the boundaries of, and well after the generated
    // windows so both admitted and rejected cases are reached frequently.
    private static readonly Gen<int> GenNowOffset = Gen.Int[-2_000, 14_000];

    // A modest requested quantity; capacity is generous so admission — not capacity — drives the outcome.
    private static readonly Gen<int> GenRequested = Gen.Int[1, 50];

    private static LoadingWindow BuildWindow(WindowSpec spec)
    {
        var start = Epoch + TimeSpan.FromSeconds(spec.StartOffsetSeconds);
        var end = start + TimeSpan.FromSeconds(spec.DurationSeconds);
        var created = LoadingWindow.Create(start, end);
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    // A small in-date inventory of the target type so an admitted, valid load can actually load
    // something (proving admission is what gates the load, not an empty inventory).
    private static List<GelLot> BuildInventory(DateTimeOffset now)
    {
        var formulation = new Formulation(new TemperatureRange(-10m, 5m), NominalShelfLife, new[] { "vanilla" });
        var lots = new List<GelLot>();
        for (var i = 0; i < 5; i++)
        {
            // Produced so that ExpiresAt is comfortably after any generated "now".
            var producedAt = now + TimeSpan.FromDays(1) - NominalShelfLife;
            var id = new GelLotId(new Guid(new byte[] { (byte)i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
            lots.Add(GelLot.Create(id, TargetType, formulation, producedAt, quantity: 10, fefoPriority: i));
        }

        return lots;
    }

    [Fact]
    public void LoadPermittedIffWithinAnyWindow_RejectedOutsideWithQuantityUnchanged()
    {
        Gen.Select(GenWindowSpecs, GenNowOffset, GenRequested)
            .Sample((windowSpecs, nowOffset, requested) =>
            {
                var windows = windowSpecs.Select(BuildWindow).ToList();
                var now = Epoch + TimeSpan.FromSeconds(nowOffset);

                var createdShip = Starship.Create(
                    StarshipId.New(),
                    cargoCapacity: 1_000,
                    ColonyId.New(),
                    windows);
                Assert.True(createdShip.IsSuccess);
                var starship = createdShip.Value;

                var loadedBefore = starship.LoadedQuantity;

                // Ground truth: is "now" inside [Start, End] of some window (inclusive)?
                var withinAnyWindow = windows.Any(w => now >= w.Start && now <= w.End);

                var lots = BuildInventory(now);
                var result = Service.TryLoad(starship, TargetType, requested, lots, now);

                if (withinAnyWindow)
                {
                    // Req 13.2: a load is permitted while within a window. With capacity and in-date
                    // inventory available, the admitted load succeeds and loads a positive amount.
                    Assert.True(result.IsSuccess);
                    Assert.True(result.Value.LoadedQuantity > 0);
                    Assert.Equal(loadedBefore + result.Value.LoadedQuantity, starship.LoadedQuantity);
                }
                else
                {
                    // Req 13.3: a load outside every window is rejected with WindowClosed and leaves the
                    // loaded quantity unchanged.
                    Assert.True(result.IsFailure);
                    Assert.Equal(ErrorKind.WindowClosed, result.Error.Kind);
                    Assert.Equal(loadedBefore, starship.LoadedQuantity);

                    // Req 13.3: when a future window exists, the error reports its start time; that start
                    // must match the earliest window strictly after "now".
                    var expectedNext = windows
                        .Where(w => w.Start > now)
                        .OrderBy(w => w.Start)
                        .FirstOrDefault();

                    if (expectedNext is not null)
                    {
                        Assert.NotNull(result.Error.Detail);
                        Assert.True(result.Error.Detail!.ContainsKey("nextWindowStart"));
                        Assert.Equal(expectedNext.Start, result.Error.Detail["nextWindowStart"]);
                    }
                }
            }, iter: Iterations);
    }
}
