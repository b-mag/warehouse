using Forge.Application.Abstractions.Commands;
using Forge.Application.Abstractions.Repositories;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;

namespace Forge.Application.Orders;

/// <summary>
/// The create-colony-order use-case handler (task 24.1, Req 9.1, 9.2, 14.2). It validates and
/// creates a <see cref="ColonyOrder"/>, then generates the fulfillment <see cref="WarehouseTask"/>s
/// (a Pick task and a Load task per order line) required to satisfy the order, and commits the whole
/// unit of work atomically.
/// <para>
/// This handler is the single entrypoint both the wired input driver's demand simulator (authoritative
/// colony demand — Req 12.2/12.4) and the REST Orders endpoint invoke through
/// <see cref="Forge.Application.Abstractions.IWarehouseCommandGateway.CreateColonyOrderAsync"/>. It
/// depends only on the Application abstractions (repositories + unit of work) and on the pure Domain
/// aggregates — never on concrete Infrastructure types or on the Simulation project (Req 9.5).
/// </para>
/// <para>
/// <b>Rejections leave state unchanged (Req 9.1).</b> Every validation runs and returns a typed
/// <see cref="Result{ColonyOrderId}"/> <em>before</em> anything is staged on a repository, and the
/// unit of work is only committed on the success path. An empty line set, a non-positive line quantity,
/// a delivery window whose end is not strictly after its start, or an unknown colony all produce a
/// rejection that persists nothing.
/// </para>
/// <para>
/// <b>Task generation is at the work-item level, not the physical level (Req 9.2, 14.2).</b> This layer
/// generates the Pick and Load <see cref="WarehouseTask"/> records that describe the work required to
/// fulfill the order — Pick tasks that will select gel lots in FEFO order, and Load tasks that will load
/// them onto a starship within its loading windows. The <em>actual</em> FEFO lot selection
/// (<see cref="Forge.Domain.Fulfillment.FefoSelector"/>) and window-admitted loading
/// (<see cref="Forge.Application.Loading.StarshipLoadingService"/>) happen when those tasks execute in
/// the per-tick pipeline (task 24.4). Consequently the generated tasks use <b>placeholder</b> origin/
/// destination cells and <b>zero</b> estimated durations here: real grid placement (via slotting) and
/// travel-time derivation (via path planning) are assigned during task execution / assignment
/// (tasks 19.1 and 24.4), not at order-creation time.
/// </para>
/// <para>
/// <b>Events.</b> No order-created domain event exists in <c>Forge.Domain.Events</c> or
/// <c>Forge.Contracts.Events</c> at this stage, so this handler publishes nothing to
/// <see cref="Forge.Application.Abstractions.IEventBus"/>. If an order-created event is later added to
/// the shared event schemas, publish it here after a successful commit.
/// </para>
/// </summary>
public sealed class CreateColonyOrderHandler
{
    // Placeholder grid cell used for both the origin and destination of generated fulfillment tasks.
    // Real cells are assigned during slotting / path planning when the tasks execute (task 24.4); the
    // WarehouseTask factory accepts any cells, so the concrete value here is immaterial to correctness.
    private static readonly Cell PlaceholderCell = new(0, 0);

    // Placeholder estimated duration for generated fulfillment tasks. The real estimate + travel time
    // are derived from the assigned agent's planned path during assignment (task 19.1); zero is a valid,
    // non-negative placeholder the WarehouseTask factory accepts (Req 15.2).
    private static readonly TimeSpan PlaceholderDuration = TimeSpan.Zero;

    private readonly IColonyRepository _colonies;
    private readonly IOrderRepository _orders;
    private readonly ITaskRepository _tasks;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>
    /// Construct the handler from the repository + unit-of-work abstractions it orchestrates (Req 9.5).
    /// </summary>
    /// <param name="colonies">Resolves the ordering colony to reject unknown colonies (Req 9.1).</param>
    /// <param name="orders">Stages the newly created order for insertion (Req 9.1).</param>
    /// <param name="tasks">Stages the generated Pick/Load fulfillment tasks for insertion (Req 9.2, 14.2).</param>
    /// <param name="unitOfWork">Commits the order + tasks atomically on the success path.</param>
    public CreateColonyOrderHandler(
        IColonyRepository colonies,
        IOrderRepository orders,
        ITaskRepository tasks,
        IUnitOfWork unitOfWork)
    {
        _colonies = colonies ?? throw new ArgumentNullException(nameof(colonies));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    /// <summary>
    /// Validate and create the colony order, generate its fulfillment tasks, and commit (Req 9.1, 9.2, 14.2).
    /// </summary>
    /// <param name="command">The requested colony, order lines, and delivery window.</param>
    /// <param name="ct">Cancellation token propagated to the repositories and unit of work.</param>
    /// <returns>
    /// A successful <see cref="Result{ColonyOrderId}"/> carrying the new order's id when the request is
    /// valid; otherwise a typed rejection (<see cref="ErrorKind.InvalidRequest"/> for empty lines or an
    /// unknown colony, <see cref="ErrorKind.Validation"/> for a non-positive line quantity or an
    /// end &lt;= start delivery window) that persists nothing and leaves state unchanged.
    /// </returns>
    public async Task<Result<ColonyOrderId>> Handle(CreateColonyOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // --- Validation (Req 9.1): run every check before staging anything so a rejection persists nothing. ---

        // Reject an order with no lines: there is nothing to fulfill.
        if (command.Lines is null || command.Lines.Count == 0)
        {
            return DomainError.InvalidRequest("A colony order must contain at least one order line.");
        }

        // Reject a delivery window whose end is not strictly after its start (mirrors the LoadingWindow
        // end > start rule). A zero-or-negative-length window cannot be fulfilled within.
        if (command.DeliveryWindowEnd <= command.DeliveryWindowStart)
        {
            return DomainError.Validation(
                $"Delivery window end ({command.DeliveryWindowEnd:o}) must be strictly after its start " +
                $"({command.DeliveryWindowStart:o}).",
                nameof(command.DeliveryWindowEnd));
        }

        // Validate each line's quantity through the domain OrderLine.Create factory (qty >= 1 — Req 5.5),
        // building the validated domain lines as we go. Any rejection short-circuits, staging nothing.
        var orderLines = new List<OrderLine>(command.Lines.Count);
        foreach (var line in command.Lines)
        {
            var lineResult = OrderLine.Create(line.GelTypeId, line.Quantity);
            if (lineResult.IsFailure)
            {
                // Surface the domain validation error as an InvalidRequest at the use-case boundary so a
                // bad quantity is reported consistently with the other request-shape rejections (Req 5.5).
                return DomainError.InvalidRequest(lineResult.Error.Message);
            }

            orderLines.Add(lineResult.Value);
        }

        // Reject an unknown colony: the order cannot be attributed to a non-existent colony (Req 9.1).
        var colony = await _colonies.GetByIdAsync(command.ColonyId, ct).ConfigureAwait(false);
        if (colony is null)
        {
            return DomainError.InvalidRequest($"Unknown colony '{command.ColonyId}'.");
        }

        // --- Creation + task generation (Req 9.1, 9.2, 14.2): only past this point do we mutate state. ---

        var orderId = ColonyOrderId.New();
        var order = new ColonyOrder(
            orderId,
            command.ColonyId,
            orderLines,
            command.DeliveryWindowStart,
            command.DeliveryWindowEnd);

        _orders.Add(order);

        // Generate the fulfillment work items (Req 9.2, 14.2): a Pick task (selects lots in FEFO order at
        // execution time) and a Load task (loads onto a starship within a loading window at execution time)
        // for each order line. Placeholder cells/durations are used here; real placement + travel time are
        // assigned during the tick/assignment pipeline (tasks 19.1, 24.4).
        foreach (var _ in orderLines)
        {
            var pick = CreateFulfillmentTask(WarehouseTaskType.Pick);
            if (pick.IsFailure)
            {
                return pick.Error;
            }

            var load = CreateFulfillmentTask(WarehouseTaskType.Load);
            if (load.IsFailure)
            {
                return load.Error;
            }

            _tasks.Add(pick.Value);
            _tasks.Add(load.Value);
        }

        // Commit the order and its generated tasks as a single unit of work (Req 9.1).
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        // No order-created event exists in the shared event schemas, so nothing is published here (see the
        // type-level remarks). Add the publish after this commit if such an event is introduced later.

        return orderId;
    }

    // Builds a single fulfillment WarehouseTask of the given type with placeholder cells + zero duration.
    // The factory validates the (non-negative) duration; a zero placeholder always succeeds, but the
    // Result is propagated rather than force-unwrapped so the invariant stays honored end to end.
    private static Result<WarehouseTask> CreateFulfillmentTask(WarehouseTaskType type) =>
        WarehouseTask.Create(
            WarehouseTaskId.New(),
            type,
            PlaceholderCell,
            PlaceholderCell,
            PlaceholderDuration);
}
