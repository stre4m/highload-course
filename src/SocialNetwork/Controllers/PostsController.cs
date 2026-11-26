using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SocialNetwork.Controllers;

[ApiController]
[Authorize]
[Route("post")]
public sealed class PostsController(FeedCache feed, PostStore posts) : ControllerBase
{
    [HttpGet("feed")]
    [ProducesResponseType<IReadOnlyList<Post>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Feed(
        [FromQuery, Range(0, int.MaxValue)] int offset = 0,
        [FromQuery, Range(1, int.MaxValue)] int limit = 10,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        return Ok(await feed.GetAsync(userId, offset, limit, ct));
    }

    [HttpPost("create")]
    [ProducesResponseType<Guid>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create(CreatePostRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (!IsValidText(request.Text))
            return BadRequest();

        return Ok(await feed.CreatePostAsync(userId, request.Text!, ct));
    }

    [HttpPut("update")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Update(UpdatePostRequest request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (request.Id == Guid.Empty || !IsValidText(request.Text))
            return BadRequest();

        return await feed.UpdatePostAsync(userId, request.Id, request.Text!, ct) ? Ok() : BadRequest();
    }

    [HttpPut("delete/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (id == Guid.Empty)
            return BadRequest();

        return await feed.DeletePostAsync(userId, id, ct) ? Ok() : BadRequest();
    }

    [AllowAnonymous]
    [HttpGet("get/{id}")]
    [ProducesResponseType<Post>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return BadRequest();

        var post = await posts.GetAsync(id, ct);
        return post is null ? NotFound() : Ok(post);
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static bool IsValidText(string? text) =>
        text is { Length: > 0 and <= 4096 } && !string.IsNullOrWhiteSpace(text) && !text.Contains('\0');
}

public sealed record CreatePostRequest(string? Text);
public sealed record UpdatePostRequest(Guid Id, string? Text);
