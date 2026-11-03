using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Catalog.Api.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sub is null || !Guid.TryParse(sub, out var id))
            throw new InvalidOperationException("Token sem identificacao de usuario.");
        return id;
    }

    public static string GetUserName(this ClaimsPrincipal user) =>
        user.FindFirstValue("name")
        ?? user.FindFirstValue(ClaimTypes.Name)
        ?? user.GetUserEmail();

    public static string GetUserEmail(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Email)
        ?? user.FindFirstValue(ClaimTypes.Email)
        ?? string.Empty;
}
