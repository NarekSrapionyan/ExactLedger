using el.Application.Security;
using el.Domain.Ledger;

namespace el.Infrastructure.Security;

internal sealed class IdentityRecord
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string[] Roles { get; set; } = [];
    public string? ClientId { get; set; }
    public PartyId? PartyId { get; set; }
    public bool Enabled { get; set; } = true;

    internal AuthenticatedIdentity ToIdentity() => new(Id.ToString("D"), Username,
        Array.AsReadOnly(Roles.ToArray()), ClientId, PartyId?.Value);
}
