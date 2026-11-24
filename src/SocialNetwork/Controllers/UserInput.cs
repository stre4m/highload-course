namespace SocialNetwork.Controllers;

internal static class UserInput
{
    public static bool ValidPassword(string? value) =>
        value is { Length: >= 8 and <= 128 } && !string.IsNullOrWhiteSpace(value);
}
