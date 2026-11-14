using el.Api.Contracts;
using el.Domain.Common;

var builder = WebApplication.CreateBuilder(args);

// Optional local overrides; user secrets, environment variables and CLI arguments win.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);

builder.Services.AddProblemDetails();

// Money is exchanged as {"amount":"250.00","currency":"USD"}; JSON numbers are rejected.
// Until versioned reference selection exists (BE-010/BE-011), the seeded version is used.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new MoneyJsonConverter(CurrencyReferenceData.Catalog, CurrencyReferenceData.InitialVersion)));

builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "ExactLedger API";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Process liveness only; database and external dependency readiness is separate.
app.MapHealthChecks("/health/live");

app.Run();

public partial class Program;
