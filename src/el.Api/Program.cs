using el.Api.Escrow;
using el.Api.Contracts;
using el.Api.Errors;
using el.Domain.Common;
using el.Infrastructure;
using el.Api.Authentication;
using el.Api.Ledger;
using el.Api.Documentation;
using el.Api.Knowledge;
using el.Api.Settlements;
using el.Api.Clearing;
using el.Api.Reporting;

var seedIdentities = args.Contains("--seed-identities", StringComparer.Ordinal);
var hostArguments = args.Where(argument => argument != "--seed-identities").ToArray();
var builder = WebApplication.CreateBuilder(hostArguments);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

// Optional local overrides; user secrets, environment variables and CLI arguments win.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Configuration.AddEnvironmentVariables().AddCommandLine(hostArguments);

if (seedIdentities)
{
    await IdentitySeedCommand.RunAsync(builder.Configuration);
    return;
}

builder.Services.AddApiErrorHandling();
builder.Services.AddTokenAuthentication();

// Money is exchanged as {"amount":"250.00","currency":"USD"}; JSON numbers are rejected.
// Until versioned reference selection exists (BE-010/BE-011), the seeded version is used.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new MoneyJsonConverter(CurrencyReferenceData.Catalog, CurrencyReferenceData.InitialVersion)));

// Read when a DbContext is first created, after all configuration sources are applied.
builder.Services.AddInfrastructure(provider => provider.GetRequiredService<IConfiguration>()
    .GetConnectionString(InfrastructureServiceCollectionExtensions.RuntimeConnectionStringName));

// Daily cut. The hosted service runs only when Clearing:Schedule:Enabled is true; the manual trigger is always available.
builder.Services.AddOptions<ClearingScheduleOptions>().BindConfiguration(ClearingScheduleOptions.SectionName)
    .Validate(options => options.TryGetTime(out _), "Clearing:Schedule:TimeUtc must use HH:mm.").ValidateOnStart();
builder.Services.AddSingleton<ClearingScheduleRunner>();

if (builder.Configuration.GetValue<bool>("Clearing:Schedule:Enabled"))
{
    builder.Services.AddHostedService<ClearingScheduleService>();
}

builder.Services.AddHealthChecks();
builder.Services.AddApiDocumentation();

var app = builder.Build();

app.UseApiErrorHandling();

if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps && context.Request.Path != "/health/live")
        {
            await ApiErrorResponses.WriteAsync(context, 400, "HTTPS_REQUIRED");
            return;
        }
        await next(context);
    });
    app.UseHsts();
}
else
{
    app.UseHttpsRedirection();
}
app.UseApiSecurity();
app.MapIdentityEndpoints();
app.MapLedgerEndpoints();
app.MapKnowledgeEndpoints();
app.MapPartyEndpoints();
app.MapSettlementEndpoints();
app.MapEscrowEndpoints();
app.MapRefundEndpoints();
app.MapClearingEndpoints();
app.MapClearingExportEndpoints();
app.MapClearingConfirmationEndpoints();
app.MapReportEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// Process liveness only; database and external dependency readiness is separate.
app.MapHealthChecks("/health/live").AllowAnonymous();

app.Run();

public partial class Program;
