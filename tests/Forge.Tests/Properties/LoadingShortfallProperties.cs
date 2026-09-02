using CsCheck;
using Forge.Application.Loading;
using Forge.Domain.Common;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 6: Loading-window-close shortfall reporting
//
// Validates: Requirements 13.6
//
// For any requested and loaded quantities at window close, the raised event SHALL report the loaded
// quantity and a shortfall equal to requested - loaded, reporting zero when fully loaded.
public sealed class LoadingShortfallProperties
{
    private const int Iterations = 100;

    private static readonly DateTimeOffset ClosedAt = new(2300, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly StarshipLoadingService Service = new();

    // Requested quantity across a broad non-negative range.
    private static readonly Gen<int> GenRequested = Gen.Int[0, 999_999];

    [Fact]
    public void WindowCloseEventReportsLoadedAndShortfall()
    {
        // Generate a requested quantity and a loaded quantity in [0, requested] so "loaded" never
        // exceeds "requested" — the domain invariant a real load sequence maintains (Req 13.5). This
        // covers the fully-loaded case (loaded == requested => shortfall 0) and every partial case.
        GenRequested
            .SelectMany(
                requested => Gen.Int[0, requested].Select(loaded => (requested, loaded)))
            .Sample(pair =>
            {
                var (requested, loaded) = pair;
                var starshipId = StarshipId.New();

                var evt = Service.CloseWindow(starshipId, requested, loaded, ClosedAt);

                // Req 13.6: the event reports the loaded quantity...
                Assert.Equal(loaded, evt.Loaded);

                // ...and a shortfall equal to requested - loaded, which is zero when fully loaded.
                Assert.Equal(requested - loaded, evt.Shortfall);
                if (loaded == requested)
                {
                    Assert.Equal(0, evt.Shortfall);
                }

                // The shortfall is always non-negative and identifies the correct starship at close time.
                Assert.True(evt.Shortfall >= 0);
                Assert.Equal(starshipId, evt.StarshipId);
                Assert.Equal(ClosedAt, evt.At);
            }, iter: Iterations);
    }

    [Fact]
    public void ShortfallIsClampedToZeroWhenLoadedExceedsRequested()
    {
        // Although the loading rule never loads more than requested, CloseWindow defensively clamps a
        // negative computed shortfall to zero so the reported shortfall is always non-negative (Req 13.6).
        Gen.Select(Gen.Int[0, 1_000], Gen.Int[1, 500])
            .Sample((requested, excess) =>
            {
                var loaded = requested + excess; // loaded strictly greater than requested
                var evt = Service.CloseWindow(StarshipId.New(), requested, loaded, ClosedAt);

                Assert.Equal(loaded, evt.Loaded);
                Assert.Equal(0, evt.Shortfall);
            }, iter: Iterations);
    }
}
