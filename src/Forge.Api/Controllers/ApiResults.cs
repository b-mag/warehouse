using Forge.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

/// <summary>
/// The transport shape of a rejected domain <see cref="DomainError"/> returned to REST clients
/// (design "Error Handling" — the Api boundary maps a typed rejection to an HTTP status + body). It
/// carries the machine-readable <see cref="Kind"/>, the human-readable <see cref="Message"/>, and any
/// structured <see cref="Detail"/> the error attached (e.g. the offending <c>parameter</c> name for a
/// validation failure — Req 20.8, 22.4).
/// </summary>
/// <param name="Kind">The <see cref="ErrorKind"/> discriminant of the rejection, as a string.</param>
/// <param name="Message">The human-readable rejection message.</param>
/// <param name="Detail">Optional structured detail (may be <c>null</c> when the error carried none).</param>
public sealed record ApiErrorDto(
    string Kind,
    string Message,
    IReadOnlyDictionary<string, object?>? Detail);

/// <summary>
/// Maps a domain <see cref="Result"/> / <see cref="Result{T}"/> rejection to the appropriate HTTP
/// response at the Api boundary, so an <em>expected</em> rejection becomes a status code + body rather
/// than a thrown exception (design "Error Handling"; Req 20.8, 22.4). A
/// <see cref="ErrorKind.Validation"/> / <see cref="ErrorKind.InvalidRequest"/> /
/// <see cref="ErrorKind.InvalidValue"/> maps to <c>400 Bad Request</c>; the remaining domain-rule
/// rejections map to <c>409 Conflict</c> (a request that is well-formed but conflicts with current
/// state). The body is always an <see cref="ApiErrorDto"/> carrying the kind, message, and detail.
/// </summary>
public static class ApiResults
{
    /// <summary>Project a <see cref="DomainError"/> to its transport <see cref="ApiErrorDto"/>.</summary>
    public static ApiErrorDto ToDto(this DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ApiErrorDto(error.Kind.ToString(), error.Message, error.Detail);
    }

    /// <summary>
    /// Build the <see cref="ObjectResult"/> for a rejection: the mapped HTTP status with the
    /// <see cref="ApiErrorDto"/> body.
    /// </summary>
    public static ObjectResult ToProblem(this DomainError error) =>
        new(error.ToDto()) { StatusCode = StatusFor(error.Kind) };

    /// <summary>The HTTP status code an <see cref="ErrorKind"/> maps to at the Api boundary.</summary>
    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.InvalidRequest => StatusCodes.Status400BadRequest,
        ErrorKind.InvalidValue => StatusCodes.Status400BadRequest,
        ErrorKind.InvalidCapacity => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status409Conflict,
    };
}
