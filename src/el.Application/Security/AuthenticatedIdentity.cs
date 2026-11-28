namespace el.Application.Security;

/// <summary>Server-owned identity data. Never construct it from a client's requested role or owner IDs.</summary>
public sealed record AuthenticatedIdentity(
    string Id,
    string Username,
    IReadOnlyList<string> Roles,
    string? ClientId = null,
    Guid? PartyId = null)
{
    public AuthenticatedIdentity(string id, string username, string role, string? clientId = null, Guid? partyId = null)
        : this(id, username, Array.AsReadOnly(new[] { role }), clientId, partyId) { }
}
