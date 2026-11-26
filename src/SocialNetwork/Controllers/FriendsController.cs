using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SocialNetwork.Controllers;

[ApiController]
[Authorize]
[Route("friend")]
public sealed class FriendsController(FeedCache feed) : ControllerBase
{
    [HttpPut("set/{user_id}")]
    [HttpPut("add/{user_id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Add([FromRoute(Name = "user_id")] Guid friendId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        if (friendId == Guid.Empty || friendId == userId)
            return BadRequest();

        return await feed.AddFriendAsync(userId, friendId, ct) ? Ok() : BadRequest();
    }

    [HttpPut("delete/{user_id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Delete([FromRoute(Name = "user_id")] Guid friendId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        if (friendId == Guid.Empty || friendId == userId)
            return BadRequest();

        await feed.DeleteFriendAsync(userId, friendId, ct);
        return Ok();
    }
}
