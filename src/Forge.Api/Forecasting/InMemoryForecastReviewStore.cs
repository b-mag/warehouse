using System.Collections.Concurrent;
using Forge.Application.Forecasting;

namespace Forge.Api.Forecasting;

/// <summary>
/// A thread-safe in-memory <see cref="IForecastReviewStore"/> (Req 22.1). The producer adds a
/// produced <see cref="ForecastLifecycle"/> as it is generated and the <c>ForecastController</c> reads
/// / decides against it on the operator's request, so a forecast is available for review immediately
/// after it is produced. Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/> so the producer
/// (a background tick) and the request threads never corrupt the set.
/// <para>
/// This is the Phase-1 process-local store; a Phase-2 durable/distributed store swaps in behind the
/// same interface. Registration in DI is the composition root's job (task 33.3).
/// </para>
/// </summary>
public sealed class InMemoryForecastReviewStore : IForecastReviewStore
{
    private readonly ConcurrentDictionary<Guid, ForecastLifecycle> _forecasts = new();

    /// <inheritdoc />
    public Guid Add(ForecastLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        var id = Guid.NewGuid();
        _forecasts[id] = lifecycle;
        return id;
    }

    /// <inheritdoc />
    public void Set(Guid id, ForecastLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        _forecasts[id] = lifecycle;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, ForecastLifecycle> GetAll() =>
        new Dictionary<Guid, ForecastLifecycle>(_forecasts);

    /// <inheritdoc />
    public bool TryGet(Guid id, out ForecastLifecycle lifecycle) =>
        _forecasts.TryGetValue(id, out lifecycle!);

    /// <inheritdoc />
    public void Update(Guid id, ForecastLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        _forecasts[id] = lifecycle;
    }
}
