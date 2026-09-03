using Forge.Application.Abstractions;
using Forge.Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

/// <summary>
/// The read-only query endpoint returning the current simulation state (Req 9.3, 23.3). It returns the
/// current <see cref="SimulationSnapshotDto"/> via <see cref="IWarehouseCommandGateway.GetSnapshotAsync"/>
/// — inventory, orders, tasks, agents, starships, metrics, and operator parameters — without mutating
/// simulation state. This is the pull counterpart to the SignalR push (task 33.1): the same snapshot
/// clients receive on connect, available on demand for polling or a cold read.
/// </summary>
[ApiController]
[Route("api/query")]
public sealed class QueryController : ControllerBase
{
    private readonly IWarehouseCommandGateway _gateway;

    /// <summary>Construct the controller over the gateway it reads the snapshot from.</summary>
    public QueryController(IWarehouseCommandGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    /// <summary>
    /// Return the current simulation snapshot (Req 9.3). Read-only; never mutates simulation state.
    /// </summary>
    /// <param name="ct">Request cancellation token.</param>
    [HttpGet("snapshot")]
    [ProducesResponseType(typeof(SimulationSnapshotDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SimulationSnapshotDto>> GetSnapshotAsync(CancellationToken ct)
    {
        var snapshot = await _gateway.GetSnapshotAsync(ct).ConfigureAwait(false);
        return Ok(snapshot);
    }
}
