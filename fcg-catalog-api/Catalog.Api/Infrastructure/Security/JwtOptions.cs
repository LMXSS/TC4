namespace Catalog.Api.Infrastructure.Security;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "FCG";
    public string Audience { get; set; } = "FCG";
}
