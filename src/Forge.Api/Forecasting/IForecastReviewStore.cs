using Forge.Application.Forecasting;

namespace Forge.Api.Forecasting;

/// <summary>
/// The Api-boundary store that makes produced <see cref="ForecastLifecycle"/>s available to the
/// operator for review and holds the reviewable set the <c>ForecastController</c> reads and decides
/// against (Req 22.1 — a produced forecast must be reviewable within 5 seconds of production).
/// <para>
/// The forecasting orchestrator produces a <see cref="ForecastState.Pending"/> forecast and
/// <see cref="Add"/>s it here under a stable id; the operator polls <see cref="GetAll"/> to review the
/// pending set, then submits a decision against a specific id which the controller resolves via
/// <see cref="TryGet"/>, applies with <see cref="SubmitForecastDecisionHandler"/>, and writes back with
/// <see cref="Update"/>. Timing (production → availability) is a non-functional target owned by the
/// producer/host; this store only exposes the current set for pull. The concrete registration lives in
/// the composition root (task 33.3); the controller depends only on this abstraction.
/// </para>
/// </summary>
public interface IForecastReviewStore
{
    /// <summary>Register a produced forecast for operator review under a new id, returning that id.</summary>
    Guid Add(ForecastLifecycle lifecycle);

    /// <summary>Store a forecast under a caller-supplied id (used to seed a known id, e.g. in tests).</summary>
    void Set(Guid id, ForecastLifecycle lifecycle);

    /// <summary>All forecasts currently available for review, with their ids.</summary>
    IReadOnlyDictionary<Guid, ForecastLifecycle> GetAll();

    /// <summary>Look up a single reviewable forecast by id; false when no such forecast exists.</summary>
    bool TryGet(Guid id, out ForecastLifecycle lifecycle);

    /// <summary>Replace the stored forecast for an id with its post-decision lifecycle.</summary>
    void Update(Guid id, ForecastLifecycle lifecycle);
}
