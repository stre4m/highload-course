using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace SocialNetwork.Controllers;

[ApiController]
[Authorize]
[Route("dialog/{user_id}")]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<DialogError>(StatusCodes.Status503ServiceUnavailable)]
public sealed class DialogsController(DialogStore dialogs, ILogger<DialogsController> logger) : ControllerBase
{
    [HttpPost("send")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Send([FromRoute(Name = "user_id")] Guid recipient, SendDialogMessageRequest request,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var sender))
            return Unauthorized();
        if (recipient == Guid.Empty || recipient == sender || request.Text is not { Length: > 0 and <= 4096 } text ||
            string.IsNullOrWhiteSpace(text) || text.Contains('\0'))
            return BadRequest();

        return await WithStorageAsync(async () =>
            await dialogs.SendAsync(sender, recipient, text, ct) ? Ok() : BadRequest());
    }

    [HttpGet("list")]
    [ProducesResponseType<IReadOnlyList<DialogMessage>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromRoute(Name = "user_id")] Guid partner, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (partner == Guid.Empty || partner == userId)
            return BadRequest();

        return await WithStorageAsync(async () =>
        {
            var messages = await dialogs.ListAsync(userId, partner, ct);
            return messages is null ? BadRequest() : Ok(messages);
        });
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private async Task<IActionResult> WithStorageAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is DialogStorageUnavailableException or NpgsqlException { IsTransient: true })
        {
            logger.LogWarning(exception, "Dialog storage is unavailable.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new DialogError("Dialog storage is unavailable.", HttpContext.TraceIdentifier));
        }
    }
}

public sealed record SendDialogMessageRequest
{
    [Required]
    public string? Text { get; init; }
}

public sealed record DialogError(string Message, string RequestId);
