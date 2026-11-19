namespace el.Application.Security;

public static class IdentityRoles
{
    public const string Integrator = "Integrator";
    public const string FinanceOperator = "FinanceOperator";
    public const string ConfigurationAdministrator = "ConfigurationAdministrator";
    public const string Payee = "Payee";
    public const string Auditor = "Auditor";
    public const string Scheduler = "Scheduler";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        Integrator, FinanceOperator, ConfigurationAdministrator, Payee, Auditor, Scheduler,
    });
}

public enum Permission
{
    SubmitTradeOrRefund,
    ReadSettlement,
    ReadBalancesAndPayouts,
    ReadLedger,
    ManageConfiguration,
    PublishConfiguration,
    ReleaseEscrow,
    RunClearingAndExport,
    ConfirmSimulatedBank,
    ReadAudit,
}

/// <summary>Exact operation grants from PRP Section 2.2. Client/party checks are additional, not replaced by roles.</summary>
public static class PermissionMatrix
{
    public static IReadOnlyList<string> RolesFor(Permission permission) => permission switch
    {
        Permission.SubmitTradeOrRefund => [IdentityRoles.Integrator],
        Permission.ReadSettlement => [IdentityRoles.Integrator, IdentityRoles.FinanceOperator,
            IdentityRoles.ConfigurationAdministrator, IdentityRoles.Payee, IdentityRoles.Auditor],
        Permission.ReadBalancesAndPayouts => [IdentityRoles.FinanceOperator, IdentityRoles.Payee, IdentityRoles.Auditor],
        Permission.ReadLedger => [IdentityRoles.FinanceOperator, IdentityRoles.Auditor],
        Permission.ManageConfiguration or Permission.PublishConfiguration => [IdentityRoles.ConfigurationAdministrator],
        Permission.ReleaseEscrow or Permission.RunClearingAndExport => [IdentityRoles.FinanceOperator, IdentityRoles.Scheduler],
        Permission.ConfirmSimulatedBank => [IdentityRoles.FinanceOperator],
        Permission.ReadAudit => [IdentityRoles.FinanceOperator, IdentityRoles.ConfigurationAdministrator, IdentityRoles.Auditor],
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

}
