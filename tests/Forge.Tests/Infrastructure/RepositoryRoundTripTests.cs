using Forge.Application.Abstractions.Repositories;
using Forge.Domain.ColdChain;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Domain.Labor;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;
using Forge.Infrastructure.Persistence;
using Forge.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainTaskStatus = Forge.Domain.Tasks.TaskStatus;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Round-trip tests for the EF Core repository implementations + <see cref="UnitOfWork"/> (task 28.2,
/// Req 9.5, 26.2). They exercise the adapter contract end to end — <c>Add</c>/<c>Update</c> stage
/// changes, <see cref="IUnitOfWork.SaveChangesAsync(System.Threading.CancellationToken)"/> commits them
/// atomically and returns the number of state entries written, and the query methods return what was
/// persisted — WITHOUT a running Postgres. The in-memory EF Core provider is used so the tests validate
/// real EF change-tracking and query behavior (not mocks); the live-Postgres migration test is the
/// optional task 28.3.
/// <para>
/// Each test builds its own context on a uniquely-named in-memory store so the cases are isolated. The
/// repositories and unit of work share one context instance, mirroring the scoped-per-request wiring the
/// composition root uses, so staged changes flow through a single unit of work.
/// </para>
/// </summary>
public sealed class RepositoryRoundTripTests
{
    private static readonly DateTimeOffset Now =
        new(2400, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ForgeDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseInMemoryDatabase($"forge-repo-{Guid.NewGuid()}")
            .Options;

        return new ForgeDbContext(options);
    }

    private static GelType MakeGelType(int minC = 0, int maxC = 10, int shelfLifeDays = 30) =>
        new(
            GelTypeId.New(),
            new Formulation(new TemperatureRange(minC, maxC), TimeSpan.FromDays(shelfLifeDays), new[] { "vanilla" }),
            velocity: 1.0);

    // ---- GelLotRepository ----

    [Fact]
    public async Task GelLot_add_then_save_persists_and_is_queryable_by_id()
    {
        await using var ctx = NewContext();
        var repo = new GelLotRepository(ctx);
        var uow = new UnitOfWork(ctx);

        var gelType = MakeGelType();
        var lot = GelLot.Create(GelLotId.New(), gelType, Now, quantity: 5);
        repo.Add(lot);

        var written = await uow.SaveChangesAsync();

        // SaveChanges reports the number of state entries written (one inserted lot).
        Assert.Equal(1, written);

        ctx.ChangeTracker.Clear();
        var fetched = await repo.GetByIdAsync(lot.Id);

        Assert.NotNull(fetched);
        Assert.Equal(lot.Id, fetched!.Id);
        Assert.Equal(gelType.Id, fetched.GelTypeId);
        Assert.Equal(5, fetched.Quantity);
    }

    [Fact]
    public async Task GelLot_get_by_id_returns_null_when_absent()
    {
        await using var ctx = NewContext();
        var repo = new GelLotRepository(ctx);

        var fetched = await repo.GetByIdAsync(GelLotId.New());

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GelLot_get_by_gel_type_returns_only_matching_lots()
    {
        await using var ctx = NewContext();
        var repo = new GelLotRepository(ctx);
        var uow = new UnitOfWork(ctx);

        var typeA = MakeGelType();
        var typeB = MakeGelType();
        repo.Add(GelLot.Create(GelLotId.New(), typeA, Now, quantity: 1));
        repo.Add(GelLot.Create(GelLotId.New(), typeA, Now, quantity: 2));
        repo.Add(GelLot.Create(GelLotId.New(), typeB, Now, quantity: 3));
        await uow.SaveChangesAsync();

        var forA = await repo.GetByGelTypeAsync(typeA.Id);
        var forB = await repo.GetByGelTypeAsync(typeB.Id);

        Assert.Equal(2, forA.Count);
        Assert.All(forA, l => Assert.Equal(typeA.Id, l.GelTypeId));
        Assert.Single(forB);
        Assert.Equal(typeB.Id, forB[0].GelTypeId);
    }

    [Fact]
    public async Task GelLot_update_after_domain_mutation_persists_the_change()
    {
        await using var ctx = NewContext();
        var repo = new GelLotRepository(ctx);
        var uow = new UnitOfWork(ctx);

        // A lot whose expiry is already in the past, so the expiry-decay rule flips it to expired.
        var gelType = MakeGelType(shelfLifeDays: 1);
        var lot = GelLot.Create(GelLotId.New(), gelType, Now.AddDays(-10), quantity: 1);
        repo.Add(lot);
        await uow.SaveChangesAsync();

        var loaded = await repo.GetByIdAsync(lot.Id);
        Assert.NotNull(loaded);
        Assert.True(loaded!.TryExpireAt(Now, out _));
        repo.Update(loaded);
        var written = await uow.SaveChangesAsync();

        Assert.Equal(1, written);

        ctx.ChangeTracker.Clear();
        var reloaded = await repo.GetByIdAsync(lot.Id);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsExpired);
    }

    [Fact]
    public async Task GelLot_list_all_returns_every_persisted_lot()
    {
        await using var ctx = NewContext();
        var repo = new GelLotRepository(ctx);
        var uow = new UnitOfWork(ctx);

        var gelType = MakeGelType();
        repo.Add(GelLot.Create(GelLotId.New(), gelType, Now, quantity: 1));
        repo.Add(GelLot.Create(GelLotId.New(), gelType, Now, quantity: 1));
        var written = await uow.SaveChangesAsync();

        var all = await repo.ListAllAsync();

        Assert.Equal(2, written);
        Assert.Equal(2, all.Count);
    }

    // ---- ZoneRepository ----

    [Fact]
    public async Task Zone_add_save_and_query_round_trips()
    {
        await using var ctx = NewContext();
        var repo = new ZoneRepository(ctx);
        var uow = new UnitOfWork(ctx);

        var zone = TemperatureZone.Create(ZoneId.New(), new TemperatureRange(0m, 10m), capacity: 100).Value;
        repo.Add(zone);
        var written = await uow.SaveChangesAsync();

        Assert.Equal(1, written);

        ctx.ChangeTracker.Clear();
        var fetched = await repo.GetByIdAsync(zone.Id);
        var all = await repo.ListAllAsync();

        Assert.NotNull(fetched);
        Assert.Equal(zone.Id, fetched!.Id);
        Assert.Single(all);
    }

    // ---- ColonyRepository ----

    [Fact]
    public async Task Colony_add_save_and_query_round_trips()
    {
        await using var ctx = NewContext();
        var repo = new ColonyRepository(ctx);
        var uow = new UnitOfWork(ctx);

        var profile = DemandProfile.Create(
            new Dictionary<GelTypeId, double> { [GelTypeId.New()] = 10.0 },
            []).Value;
        var colony = new Colony(ColonyId.New(), profile);
        repo.Add(colony);
        var written = await uow.SaveChangesAsync();

        Assert.Equal(1, written);

        ctx.ChangeTracker.Clear();
        var fetched = await repo.GetByIdAsync(colony.Id);

        Assert.NotNull(fetched);
        Assert.Equal(colony.Id, fetched!.Id);
    }

    // ---- OrderRepository ----

    [Fact]
    public async Task Order_add_save_and_query_by_colony_round_trips()
    {
        await using var ctx = NewContext();
        var repo = new OrderRepository(ctx);
        var uow = new UnitOfWork(ctx);

        var colonyA = ColonyId.New();
        var colonyB = ColonyId.New();
        var line = new[] { OrderLine.Create(GelTypeId.New(), quantity: 3).Value };

        repo.Add(new ColonyOrder(ColonyOrderId.New(), colonyA, line, Now, Now.AddHours(4)));
        repo.Add(new ColonyOrder(ColonyOrderId.New(), colonyA, line, Now, Now.AddHours(4)));
        repo.Add(new ColonyOrder(ColonyOrderId.New(), colonyB, line, Now, Now.AddHours(4)));
        await uow.SaveChangesAsync();

        var forA = await repo.GetByColonyAsync(colonyA);
        var all = await repo.ListAllAsync();

        Assert.Equal(2, forA.Count);
        Assert.All(forA, o => Assert.Equal(colonyA, o.Colony));
        Assert.Equal(3, all.Count);
    }

    // ---- TaskRepository ----

    [Fact]
    public async Task Task_get_unassigned_returns_only_created_or_queued_tasks()
    {
        await using var ctx = NewContext();
        var repo = new TaskRepository(ctx);
        var uow = new UnitOfWork(ctx);

        // Created (unassigned by default).
        var created = WarehouseTask.Create(
            WarehouseTaskId.New(), WarehouseTaskType.Pick, new Cell(0, 0), new Cell(1, 1), TimeSpan.FromMinutes(5)).Value;

        // Queued.
        var queued = WarehouseTask.Create(
            WarehouseTaskId.New(), WarehouseTaskType.PutAway, new Cell(0, 0), new Cell(1, 1), TimeSpan.FromMinutes(5)).Value;
        Assert.True(queued.Queue().IsSuccess);

        // Assigned (not awaiting assignment).
        var assigned = WarehouseTask.Create(
            WarehouseTaskId.New(), WarehouseTaskType.Load, new Cell(0, 0), new Cell(1, 1), TimeSpan.FromMinutes(5)).Value;
        Assert.True(assigned.AssignTo(WorkerId.New()).IsSuccess);

        repo.Add(created);
        repo.Add(queued);
        repo.Add(assigned);
        await uow.SaveChangesAsync();

        var unassigned = await repo.GetUnassignedAsync();

        Assert.Equal(2, unassigned.Count);
        Assert.All(unassigned, t =>
            Assert.True(t.Status is DomainTaskStatus.Created or DomainTaskStatus.Queued));
    }

    // ---- WorkerRepository ----

    [Fact]
    public async Task Worker_get_on_shift_filters_by_the_domain_predicate()
    {
        await using var ctx = NewContext();
        var repo = new WorkerRepository(ctx);
        var uow = new UnitOfWork(ctx);

        var onShift = Worker.Create(
            WorkerId.New(),
            hourlyRate: 25m,
            new[] { WorkerShift.Create(Now.AddHours(-1), Now.AddHours(8)).Value }).Value;

        var offShift = Worker.Create(
            WorkerId.New(),
            hourlyRate: 25m,
            new[] { WorkerShift.Create(Now.AddHours(10), Now.AddHours(18)).Value }).Value;

        repo.Add(onShift);
        repo.Add(offShift);
        var written = await uow.SaveChangesAsync();

        var result = await repo.GetOnShiftAsync(Now);
        var all = await repo.ListAllAsync();

        Assert.Equal(2, written);
        Assert.Equal(2, all.Count);
        Assert.Single(result);
        Assert.Equal(onShift.Id, result[0].Id);
    }

    // ---- UnitOfWork ----

    [Fact]
    public async Task UnitOfWork_returns_zero_when_no_changes_are_staged()
    {
        await using var ctx = NewContext();
        var uow = new UnitOfWork(ctx);

        var written = await uow.SaveChangesAsync();

        Assert.Equal(0, written);
    }

    [Fact]
    public async Task UnitOfWork_commits_changes_from_multiple_repositories_together()
    {
        await using var ctx = NewContext();
        var lots = new GelLotRepository(ctx);
        var zones = new ZoneRepository(ctx);
        var uow = new UnitOfWork(ctx);

        var gelType = MakeGelType();
        lots.Add(GelLot.Create(GelLotId.New(), gelType, Now, quantity: 1));
        zones.Add(TemperatureZone.Create(ZoneId.New(), new TemperatureRange(0m, 10m), capacity: 50).Value);

        // A single unit of work commits changes staged across both repositories atomically.
        var written = await uow.SaveChangesAsync();

        Assert.Equal(2, written);
        Assert.Single(await lots.ListAllAsync());
        Assert.Single(await zones.ListAllAsync());
    }
}
