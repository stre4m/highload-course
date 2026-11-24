using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace SocialNetwork.Controllers;

[ApiController]
public sealed class AuthController(
    UserStore users,
    PasswordHasher<string> hasher,
    DummyPasswordHash dummyHash,
    IOptionsMonitor<BearerTokenOptions> tokenOptions) : ControllerBase
{
    [HttpPost("/login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        if (request.Id is not { } id || id == Guid.Empty || !UserInput.ValidPassword(request.Password))
            return BadRequest(new { error = "Invalid login data." });

        var hash = await users.GetPasswordHashAsync(id, ct);
        var verified = hasher.VerifyHashedPassword(id.ToString(), hash ?? dummyHash.Value, request.Password!);
        if (hash is null || verified == PasswordVerificationResult.Failed)
            return NotFound();

        var scheme = BearerTokenDefaults.AuthenticationScheme;
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], scheme);
        var options = tokenOptions.Get(scheme);
        var properties = new AuthenticationProperties
        {
            ExpiresUtc = DateTimeOffset.UtcNow + options.BearerTokenExpiration
        };
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), properties, $"{scheme}:AccessToken");
        var token = options.BearerTokenProtector.Protect(ticket);
        Response.Headers.CacheControl = "no-store";
        return Ok(new LoginResponse(token));
    }
}

public sealed record LoginResponse(string Token);
public sealed record LoginRequest(Guid? Id, string? Password);
public sealed record DummyPasswordHash(string Value);
