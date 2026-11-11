using System.Security.Claims;

public static class UserIds
{
    public static string Get(ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "anonymous";

    public static string Name(ClaimsPrincipal user) =>
        user.Identity?.Name
        ?? user.FindFirst("preferred_username")?.Value
        ?? "anonymous";
}
