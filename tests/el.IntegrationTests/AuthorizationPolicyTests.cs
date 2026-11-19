using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using el.Api.Authentication;
using el.Api.Contracts;
using el.Api.Errors;
using el.Application.Security;
using el.Domain.Common;
using el.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace el.IntegrationTests;

public sealed class AuthorizationPolicyTests
{
    private static readonly Guid PartyA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PartyB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Independent expected matrix: PRP page 3, columns in Permission enum order.
    public static TheoryData<string, bool[]> RoleMatrix => new()
    {
        { "Integrator", [true, true, false, false, false, false, false, false, false, false] },
        { "FinanceOperator", [false, true, true, true, false, false, true, true, true, true] },
        { "ConfigurationAdministrator", [false, true, false, false, true, true, false, false, false, true] },
        { "Payee", [false, true, true, false, false, false, false, false, false, false] },
        { "Auditor", [false, true, true, true, false, false, false, false, false, true] },
        { "Scheduler", [false, false, false, false, false, false, true, true, false, false] },
    };

    [Theory]
    [MemberData(nameof(RoleMatrix))]
    public async Task Every_role_has_exactly_the_PRP_grants_over_HTTP(string role, bool[] expected)
    {
        using var host = CreateHost();
        using var client = ClientFor(host, new("actor", role, role, "client-A", PartyA));
        var permissions = Enum.GetValues<Permission>();
        Assert.Equal(expected.Length, permissions.Length);
        for (var index = 0; index < permissions.Length; index++)
        {
            using var response = await client.GetAsync("/permission/" + permissions[index]);
            Assert.Equal(expected[index] ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var unclassified = await client.GetAsync("/unclassified");
        Assert.Equal(HttpStatusCode.Forbidden, unclassified.StatusCode);
        using var download = await client.GetAsync("/batch.csv");
        Assert.Equal(role is "FinanceOperator" or "Scheduler" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, download.StatusCode);
    }

    [Fact]
    public async Task Combined_roles_grant_the_union_without_an_admin_bypass()
    {
        using var host = CreateHost();
        using var client = ClientFor(host, new("combined", "demo-admin",
            new[] { IdentityRoles.ConfigurationAdministrator, IdentityRoles.FinanceOperator }));
        using var publish = await client.GetAsync("/permission/PublishConfiguration");
        using var confirm = await client.GetAsync("/permission/ConfirmSimulatedBank");
        using var submit = await client.GetAsync("/permission/SubmitTradeOrRefund");
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, submit.StatusCode);
    }

    [Fact]
    public async Task Integration_identity_cannot_select_another_client_using_request_parameters()
    {
        using var host = CreateHost();
        using var client = ClientFor(host, new("integrator", "integrator", "Integrator", "client-A"));
        using var own = await client.GetAsync("/settlement/own");
        using var foreign = await client.GetAsync("/settlement/foreign?clientId=client-A");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var ownCommand = await client.PostAsJsonAsync("/settlement/own/refund", new { clientId = "client-A" });
        using var foreignCommand = await client.PostAsJsonAsync("/settlement/foreign/refund", new { clientId = "client-A" });
        Assert.Equal(HttpStatusCode.OK, ownCommand.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignCommand.StatusCode);
    }

    [Fact]
    public async Task Payee_HTTP_view_omits_other_parties_global_trace_and_all_payout_references()
    {
        using var host = CreateHost();
        using var client = ClientFor(host, new("payee", "payee", "Payee", partyId: PartyA));
        using var own = await client.GetAsync("/settlement/own?partyId=" + PartyB);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var json = await own.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("allocations").GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("trace").GetArrayLength());
        Assert.DoesNotContain(PartyB.ToString("D"), json, StringComparison.Ordinal);
        Assert.DoesNotContain("payoutReference", json, StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER-PARTY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("GLOBAL-TRACE", json, StringComparison.Ordinal);
        using var foreign = await client.GetAsync("/settlement/foreign?partyId=" + PartyA);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static HttpClient ClientFor(IHost host, AuthenticatedIdentity identity)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            host.Services.GetRequiredService<IAccessTokenIssuer>().Issue(identity).Value);
        return client;
    }

    private static IHost CreateHost() => new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
        .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{JwtSettings.SectionName}:Issuer"] = "authorization-tests",
            [$"{JwtSettings.SectionName}:Audience"] = "authorization-clients",
            [$"{JwtSettings.SectionName}:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
        }))
        .ConfigureServices(services =>
        {
            services.AddRouting(); services.AddApiErrorHandling(); services.AddTokenAuthentication();
            services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(
                new MoneyJsonConverter(CurrencyReferenceData.Catalog, CurrencyReferenceData.InitialVersion)));
        })
        .Configure(app =>
        {
            app.UseApiErrorHandling(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                foreach (var permission in Enum.GetValues<Permission>())
                    endpoints.MapGet("/permission/" + permission, () => Results.Ok()).RequireAuthorization(permission.ToString());
                endpoints.MapGet("/unclassified", () => Results.Ok());
                endpoints.MapGet("/batch.csv", () => Results.File("synthetic export"u8.ToArray(), "text/csv"))
                    .RequireAuthorization(Permission.RunClearingAndExport.ToString());
                endpoints.MapPost("/settlement/{id}/refund", (string id, HttpContext context) =>
                    ResourceAccess.Client(RequestIdentity.Get(context), id == "own" ? "client-A" : "client-B")
                        ? Results.Ok() : Results.Problem(ApiErrorResponses.Create(context, 404)))
                    .RequireAuthorization(Permission.SubmitTradeOrRefund.ToString());
                // Contract harness for future settlement/artifact routes. These are not shipped business endpoints.
                endpoints.MapGet("/settlement/{id}", (string id, HttpContext context) =>
                {
                    var amount = Money.FromMinorUnits(100, CurrencyReferenceData.Usd);
                    PartyAllocationRow[] rows = id == "own"
                        ? [new(PartyA, "payable", amount, "OWN-PAYOUT"), new(PartyB, "OTHER-PARTY", amount, "OTHER-PAYOUT")]
                        : [new(PartyB, "OTHER-PARTY", amount, "OTHER-PAYOUT")];
                    PartyTraceRow[] trace = [new(PartyA, "own-escrow", amount), new(PartyB, "OTHER-PARTY", amount), new(null, "GLOBAL-TRACE", amount)];
                    var actor = RequestIdentity.Get(context);
                    return ResourceAccess.Settlement(actor, id == "own" ? "client-A" : "client-B", rows.Select(row => row.PartyId).ToArray()) switch
                    {
                        SettlementReadAccess.Full => Results.Ok(new { allocations = rows, trace }),
                        SettlementReadAccess.OwnParty => Results.Ok(PayeeSettlementProjection.Create(actor.PartyId!.Value, rows, trace)),
                        _ => Results.Problem(ApiErrorResponses.Create(context, 404)),
                    };
                }).RequireAuthorization(Permission.ReadSettlement.ToString());
            });
        })).Start();
}
