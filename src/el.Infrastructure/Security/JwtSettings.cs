namespace el.Infrastructure.Security;

public sealed class JwtSettings
{
    public const string SectionName = "Authentication:Jwt";
    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string SigningKey { get; init; } = string.Empty;

    public bool HasValidSigningKey()
    {
        try { return Convert.FromBase64String(SigningKey).Length >= 32; }
        catch (FormatException) { return false; }
    }
}
