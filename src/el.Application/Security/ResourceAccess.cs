namespace el.Application.Security;

public enum SettlementReadAccess { None, Full, OwnParty }

public static class ResourceAccess
{
    /// <summary>ownerClientId and participatingPartyIds must come from stored data, never the request.</summary>
    public static SettlementReadAccess Settlement(AuthenticatedIdentity identity, string ownerClientId,
        IReadOnlyCollection<Guid> participatingPartyIds)
    {
        if (identity.Roles.Any(role => role is IdentityRoles.FinanceOperator or IdentityRoles.ConfigurationAdministrator or IdentityRoles.Auditor))
            return SettlementReadAccess.Full;
        if (identity.Roles.Contains(IdentityRoles.Integrator) && identity.ClientId is not null
            && string.Equals(identity.ClientId, ownerClientId, StringComparison.Ordinal))
            return SettlementReadAccess.Full;
        if (identity.Roles.Contains(IdentityRoles.Payee) && identity.PartyId is { } partyId && participatingPartyIds.Contains(partyId))
            return SettlementReadAccess.OwnParty;
        return SettlementReadAccess.None;
    }

    public static bool Party(AuthenticatedIdentity identity, Guid? ownerPartyId) =>
        identity.Roles.Any(role => role is IdentityRoles.FinanceOperator or IdentityRoles.Auditor)
        || (ownerPartyId is not null && identity.Roles.Contains(IdentityRoles.Payee) && identity.PartyId == ownerPartyId);

    public static bool Client(AuthenticatedIdentity identity, string ownerClientId) =>
        identity.Roles.Contains(IdentityRoles.Integrator) && identity.ClientId is not null
        && string.Equals(identity.ClientId, ownerClientId, StringComparison.Ordinal);
}
