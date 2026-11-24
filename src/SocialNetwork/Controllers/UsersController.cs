using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace SocialNetwork.Controllers;

[ApiController]
[Route("user")]
public sealed class UsersController(UserStore users, PasswordHasher<string> hasher) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType<RegisterResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var firstName = request.FirstName?.Trim();
        var secondName = request.SecondName?.Trim();
        var gender = request.Gender?.Trim();
        var biography = request.Biography?.Trim();
        var city = request.City?.Trim();
        if (!ValidText(firstName, 100) || !ValidText(secondName, 100) ||
            !ValidText(gender, 50) || !ValidText(biography, 2000) || !ValidText(city, 100) ||
            request.Birthdate is not { } birthdate || birthdate > DateOnly.FromDateTime(DateTime.UtcNow) ||
            !UserInput.ValidPassword(request.Password))
            return BadRequest(new { error = "Invalid registration data." });

        var profile = new Profile(Guid.NewGuid(), firstName!, secondName!, birthdate, gender!, biography!, city!);
        var hash = hasher.HashPassword(profile.Id.ToString(), request.Password!);
        await users.InsertAsync(profile, hash, ct);
        return Ok(new RegisterResponse(profile.Id));
    }

    [HttpGet("get/{id}")]
    [ProducesResponseType<Profile>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            return BadRequest();

        var profile = await users.GetAsync(id, ct);
        return profile is null ? NotFound() : Ok(profile);
    }

    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType<Profile>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return Unauthorized();

        var profile = await users.GetAsync(id, ct);
        return profile is null ? NotFound() : Ok(profile);
    }

    private static bool ValidText(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength && !value.Contains('\0');
}

public sealed record RegisterResponse(Guid UserId);
public sealed record RegisterRequest(string? FirstName, string? SecondName, DateOnly? Birthdate, string? Gender,
    string? Biography, string? City, string? Password);
