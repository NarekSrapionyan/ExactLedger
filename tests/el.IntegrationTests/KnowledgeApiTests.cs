using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using el.Api.Contracts;
using el.Api.Knowledge;
using el.Application.Knowledge;
using el.Domain.Common;
using el.Infrastructure;
using el.Infrastructure.Persistence;
using el.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace el.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", PostgreSqlCollection.Category)]
public sealed class KnowledgeApiTests(PostgreSqlFixture database)
{
    private static readonly JsonSerializerOptions Json = Options();
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Next = From.AddYears(1);
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new MoneyJsonConverter(CurrencyReferenceData.Catalog, CurrencyReferenceData.InitialVersion));
        return options;
    }
    private static Money Usd(long minor) => Money.FromMinorUnits(minor, CurrencyReferenceData.Usd);
    private static RuleRequest Rule(string region = "DEMO-A", string type = "DEMO-LEVY", int sequence = 1) =>
        new(Guid.NewGuid(), 1, region, type, "Gross", 5_000_000, sequence,
            KnowledgeSeed.Rules[0].BeneficiaryAccountId, "USD", "HalfEven", From, Next, "Synthetic test rule", true,
            [new("Synthetic 5% of 100.00", Usd(10000), Usd(500))]);
    private static ProfileRequest Profile(string corporate = "TEST-PROFILE")
    {
        var p = KnowledgeSeed.Profiles[0];
        return new(Guid.NewGuid(), 1, p.ClientId, corporate, "USD", 10_000_000, 2_900_000, Usd(30), Usd(50), Usd(500),
            "HalfEven", 10_000_000, 7, p.CommissionAccountId, p.ProcessorAccountId, From, Next,
            "Synthetic test profile", true, [new("Synthetic expected components", Usd(10000), Usd(8000), Usd(1000), Usd(320), Usd(800), Usd(7200))]);
    }
    private static async Task<HttpResponseMessage> SendAsync(IdentityApiHost host, string user, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await host.TokenAsync(user));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await host.Client.SendAsync(request);
    }
    private static async Task<RuleResponse> DraftRuleAsync(IdentityApiHost host, RuleRequest request)
    {
        using var response = await SendAsync(host, "admin", HttpMethod.Post, "/v1/rules", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RuleResponse>(Json))!;
    }
    private static async Task<ProfileResponse> DraftProfileAsync(IdentityApiHost host, ProfileRequest request)
    {
        using var response = await SendAsync(host, "admin", HttpMethod.Post, "/v1/fee-profiles", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProfileResponse>(Json))!;
    }

    [Fact]
    public async Task Migrated_synthetic_dataset_is_labeled_and_every_published_fixture_passes()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        await using var provider = new ServiceCollection().AddInfrastructure(host.RuntimeConnection).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IKnowledgeStore>();
        Assert.Equal(3, (await store.RegionsAsync(default)).Count);
        var service = scope.ServiceProvider.GetRequiredService<KnowledgeService>();
        foreach (var v in await store.RulesAsync(default))
        {
            Assert.True(v.Definition.IsSynthetic); Assert.Contains("Synthetic", v.Definition.SourceNote);
            Assert.Equal("synthetic-seed", v.Definition.PublishedBy);
            await service.ValidateRuleAsync(v.Definition.Id, default);
        }
        foreach (var v in await store.ProfilesAsync(default))
        {
            Assert.True(v.Definition.IsSynthetic); Assert.Contains("Synthetic", v.Definition.SourceNote);
            await service.ValidateProfileAsync(v.Definition.Id, default);
        }
        var profile = KnowledgeSeed.Profiles[0];
        var a = await service.SelectAsync("DEMO-A", profile.ProfileId, profile.ClientId, profile.CorporateCode, "USD", From, default);
        var b = await service.SelectAsync("DEMO-B", profile.ProfileId, profile.ClientId, profile.CorporateCode, "USD", From, default);
        var none = await service.SelectAsync("DEMO-NONE", profile.ProfileId, profile.ClientId, profile.CorporateCode, "USD", From, default);
        Assert.Equal(1000, Assert.Single(a.Rules).Calculate(10000));
        Assert.Equal(500, Assert.Single(b.Rules).Calculate(10000));
        Assert.Empty(none.Rules); Assert.Equal("NO_DEDUCTION", none.RegionalOutcome);
        Assert.Equal("UNKNOWN_REGION", (await Assert.ThrowsAsync<KnowledgeException>(() =>
            service.SelectAsync("UNKNOWN", profile.ProfileId, profile.ClientId, profile.CorporateCode, "USD", From, default))).Code);
    }

    [Fact]
    public async Task Real_HTTP_routes_allow_only_admin_writes_and_admin_or_auditor_configuration_reads()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        foreach (var user in new[] { "integrator", "finance", "admin", "auditor", "payee-seller-1", "scheduler", "demo-admin" })
        {
            foreach (var path in new[] { "/v1/regions", "/v1/deduction-types", "/v1/rules", "/v1/fee-profiles" })
            {
                using var read = await SendAsync(host, user, HttpMethod.Get, path);
                var expected = user is "admin" or "auditor" or "demo-admin" ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
                Assert.True(read.StatusCode == expected, $"{user} GET {path}: expected {expected}, got {read.StatusCode}: {await read.Content.ReadAsStringAsync()}");
            }
            using var write = await SendAsync(host, user, HttpMethod.Post, "/v1/regions", new RegionRequest("R-" + Guid.NewGuid().ToString("N").ToUpperInvariant(), "Synthetic region"));
            Assert.Equal(user is "admin" or "demo-admin" ? HttpStatusCode.Created : HttpStatusCode.Forbidden, write.StatusCode);
            using var publish = await SendAsync(host, user, HttpMethod.Post, $"/v1/rules/{KnowledgeSeed.Rules[0].Id}/publish");
            Assert.Equal(user is "admin" or "demo-admin" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, publish.StatusCode);
        }
        using var anonymous = await host.Client.GetAsync("/v1/rules");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Admin_can_create_taxonomy_edit_a_draft_validate_and_publish_with_transactional_actor_audit()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        using var region = await SendAsync(host, "admin", HttpMethod.Post, "/v1/regions", new RegionRequest("TEST-A", "Synthetic new region"));
        using var type = await SendAsync(host, "admin", HttpMethod.Post, "/v1/deduction-types", new DeductionTypeRequest("TEST-LEVY", "Synthetic levy", "Synthetic assumption"));
        Assert.Equal(HttpStatusCode.Created, region.StatusCode); Assert.Equal(HttpStatusCode.Created, type.StatusCode);
        var draft = await DraftRuleAsync(host, Rule("TEST-A", "TEST-LEVY"));
        Assert.Null(draft.PublishedAtUtc);
        using var edit = await SendAsync(host, "admin", HttpMethod.Put, $"/v1/rules/{draft.Id}", draft.Content with { SourceNote = "Updated synthetic assumption" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        using var validate = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{draft.Id}/validate");
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        using var publish = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{draft.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent, publish.StatusCode);
        Assert.Equal(1L, await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.audit_events WHERE operation = 'rule.published' AND target_id = '{draft.Id}' AND actor_id <> 'system' AND correlation_id <> ''"));
        using var immutable = await SendAsync(host, "admin", HttpMethod.Put, $"/v1/rules/{draft.Id}", draft.Content);
        Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);
        using var republish = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{draft.Id}/publish");
        Assert.Equal(HttpStatusCode.Conflict, republish.StatusCode);
    }

    [Fact]
    public async Task Failed_examples_leave_the_draft_unpublished_and_can_be_corrected()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var good = Rule("DEMO-NONE");
        var draft = await DraftRuleAsync(host, good with { Fixtures = [new("Deliberately wrong expected result", Usd(10000), Usd(499))] });
        foreach (var operation in new[] { "validate", "publish" })
        {
            using var failed = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{draft.Id}/{operation}");
            Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
            using var problem = JsonDocument.Parse(await failed.Content.ReadAsStringAsync());
            Assert.Equal("FIXTURE_FAILED", problem.RootElement.GetProperty("code").GetString());
        }
        Assert.Equal(0L, await host.ScalarAsync<long>($"SELECT count(*) FROM ledger.audit_events WHERE operation = 'rule.published' AND target_id = '{draft.Id}'"));
        Assert.True(await host.ScalarAsync<bool>($"SELECT published_at_utc IS NULL FROM ledger.deduction_rules WHERE id = '{draft.Id}'"));
        using var edit = await SendAsync(host, "admin", HttpMethod.Put, $"/v1/rules/{draft.Id}", good);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        using var publish = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{draft.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent, publish.StatusCode);
    }

    [Fact]
    public async Task Rejects_overlaps_duplicate_sequences_and_non_adjacent_replacements()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var overlap = await DraftRuleAsync(host, Rule());
        using var overlapResult = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{overlap.Id}/publish");
        Assert.Equal(HttpStatusCode.Conflict, overlapResult.StatusCode);
        using var type = await SendAsync(host, "admin", HttpMethod.Post, "/v1/deduction-types", new DeductionTypeRequest("SECOND", "Synthetic second type", "Synthetic assumption"));
        var sequence = await DraftRuleAsync(host, Rule(type: "SECOND"));
        using var duplicate = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{sequence.Id}/publish");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var reorder = await SendAsync(host, "admin", HttpMethod.Put, $"/v1/rules/{sequence.Id}", sequence.Content with { Sequence = 2 });
        Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);
        using var ordered = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{sequence.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent, ordered.StatusCode);
        var gap = await DraftRuleAsync(host, Rule() with { RuleId = KnowledgeSeed.Rules[0].RuleId, Version = 2, EffectiveFrom = Next.AddDays(1), EffectiveTo = Next.AddYears(1) });
        using var gapResult = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{gap.Id}/publish");
        Assert.Equal(HttpStatusCode.Conflict, gapResult.StatusCode);
        using var duplicateVersion = await SendAsync(host, "admin", HttpMethod.Post, "/v1/rules", gap.Content);
        Assert.Equal(HttpStatusCode.Conflict, duplicateVersion.StatusCode);
    }

    [Fact]
    public async Task Adjacent_rule_and_profile_versions_affect_only_newly_eligible_selection_and_keep_old_snapshot()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var original = KnowledgeSeed.Profiles[0];
        await using var provider = new ServiceCollection().AddInfrastructure(host.RuntimeConnection).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<KnowledgeService>();
        var old = await service.SelectAsync("DEMO-A", original.ProfileId, original.ClientId, original.CorporateCode, "USD", Next.AddTicks(-1), default);
        var profile = await DraftProfileAsync(host, Profile(original.CorporateCode) with
        {
            ProfileId = original.ProfileId, Version = 2, EffectiveFrom = Next, EffectiveTo = Next.AddYears(1), CommissionRateUnits = 20_000_000,
            Fixtures = [new("Synthetic version 2", Usd(10000), Usd(8000), Usd(2000), Usd(320), Usd(800), Usd(7200))],
        });
        using var profilePublish = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/fee-profiles/{profile.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent, profilePublish.StatusCode);
        var rule = await DraftRuleAsync(host, Rule() with
        {
            RuleId = KnowledgeSeed.Rules[0].RuleId, Version = 2, EffectiveFrom = Next, EffectiveTo = Next.AddYears(1), RateUnits = 15_000_000,
            Fixtures = [new("Synthetic version 2", Usd(10000), Usd(1500))],
        });
        using var rulePublish = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/rules/{rule.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent, rulePublish.StatusCode);
        var current = await service.SelectAsync("DEMO-A", original.ProfileId, original.ClientId, original.CorporateCode, "USD", Next, default);
        var replay = await service.SelectAsync("DEMO-A", original.ProfileId, original.ClientId, original.CorporateCode, "USD", Next.AddTicks(-1), default);
        Assert.Equal(1000, old.Profile.Calculate(10000, 8000).CommissionMinor);
        Assert.Equal(2000, current.Profile.Calculate(10000, 8000).CommissionMinor);
        Assert.Equal(1000, Assert.Single(old.Rules).Calculate(10000));
        Assert.Equal(1500, Assert.Single(current.Rules).Calculate(10000));
        Assert.Equal(old.Profile, replay.Profile); Assert.Equal(old.Rules, replay.Rules);
        foreach (var (client, corporate, currency, time) in new[]
        { ("foreign", original.CorporateCode, "USD", Next), (original.ClientId, "FOREIGN", "USD", Next),
          (original.ClientId, original.CorporateCode, "JPY", Next), (original.ClientId, original.CorporateCode, "USD", Next.AddYears(1)) })
            Assert.Equal("PROFILE_NOT_APPLICABLE", (await Assert.ThrowsAsync<KnowledgeException>(() =>
                service.SelectAsync("DEMO-A", original.ProfileId, client, corporate, currency, time, default))).Code);
    }

    [Fact]
    public async Task Invalid_ranges_examples_and_account_mappings_are_rejected_before_publication()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        foreach (var request in new[]
        {
            Rule() with { RateUnits = -1 }, Rule() with { EffectiveTo = From }, Rule() with { RegionCode = "UNKNOWN" },
            Rule() with { BeneficiaryAccountId = KnowledgeSeed.Profiles[0].CommissionAccountId }, Rule() with { Fixtures = [] },
            Rule() with { Currency = "JPY" }, Rule() with { Fixtures = [new("Wrong currency", Money.FromMinorUnits(1, CurrencyReferenceData.Jpy), Usd(1))] },
        })
        {
            using var response = await SendAsync(host, "admin", HttpMethod.Post, "/v1/rules", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var request in new[]
        {
            Profile() with { ProcessorFloor = Usd(501) }, Profile() with { HoldDays = 31 }, Profile() with { HoldDays = -1 },
            Profile() with { ProcessorAccountId = KnowledgeSeed.Profiles[0].CommissionAccountId }, Profile() with { RoundingMode = "1" },
        })
        {
            using var response = await SendAsync(host, "admin", HttpMethod.Post, "/v1/fee-profiles", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Optional_fee_bounds_accept_null_and_retain_the_strict_money_contract_for_values()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var draft = await DraftProfileAsync(host, Profile() with { ProcessorFloor = null, ProcessorCap = null });
        Assert.Null(draft.Content.ProcessorFloor); Assert.Null(draft.Content.ProcessorCap);
        using var validate = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/fee-profiles/{draft.Id}/validate");
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        using var publish = await SendAsync(host, "admin", HttpMethod.Post, $"/v1/fee-profiles/{draft.Id}/publish");
        Assert.Equal(HttpStatusCode.NoContent, publish.StatusCode);
    }

    [Fact]
    public async Task Concurrent_overlapping_profile_publications_commit_one_version_and_one_audit_event()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        var a = await DraftProfileAsync(host, Profile("RACE"));
        var b = await DraftProfileAsync(host, Profile("RACE"));
        var results = await Task.WhenAll(SendAsync(host, "admin", HttpMethod.Post, $"/v1/fee-profiles/{a.Id}/publish"),
            SendAsync(host, "admin", HttpMethod.Post, $"/v1/fee-profiles/{b.Id}/publish"));
        try { Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }, results.Select(item => item.StatusCode).Order().ToArray()); }
        finally { foreach (var result in results) result.Dispose(); }
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.fee_profiles WHERE corporate_code = 'RACE' AND published_at_utc IS NOT NULL"));
        Assert.Equal(1L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events WHERE operation = 'profile.published'"));
    }

    [Fact]
    public async Task Database_guards_published_content_fixtures_intervals_and_overlap_even_for_owner()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        foreach (var sql in new[]
        {
            "UPDATE ledger.deduction_rules SET rate_units = 0", "UPDATE ledger.fee_profiles SET effective_to = effective_to + interval '1 day'",
            "DELETE FROM ledger.deduction_rules", "DELETE FROM ledger.fee_profiles", "UPDATE ledger.rule_fixtures SET expected_minor = 0",
            "DELETE FROM ledger.profile_fixtures", "UPDATE ledger.profile_fixtures SET expected_payable_minor = 0",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync(sql));
            Assert.Equal("EL201", error.SqlState);
        }
        var draft = await DraftRuleAsync(host, Rule());
        var conflict = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync($"UPDATE ledger.deduction_rules SET published_by = 'synthetic-test', published_at_utc = now() WHERE id = '{draft.Id}'"));
        Assert.Equal("EL202", conflict.SqlState);
        var interval = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync($"UPDATE ledger.deduction_rules SET effective_to = effective_from WHERE id = '{draft.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, interval.SqlState);
        foreach (var table in new[] { "deduction_rules", "fee_profiles", "rule_fixtures", "profile_fixtures" })
        {
            var truncate = await Assert.ThrowsAsync<PostgresException>(() => host.ExecuteAsync($"TRUNCATE ledger.{table} CASCADE"));
            Assert.Equal(LedgerDatabase.HistoryImmutable, truncate.SqlState);
        }
    }

    [Fact]
    public async Task Knowledge_migration_can_roll_back_and_reapply_without_changing_existing_identities_or_accounts()
    {
        await using var host = await IdentityApiHost.CreateAsync(database);
        await using var context = LedgerSchema.NewContext(host.AdminConnection);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261116152549_IdentityAndAudit");
        Assert.True(await host.ScalarAsync<bool>("SELECT to_regclass('ledger.deduction_rules') IS NULL"));
        Assert.Equal(9L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.identities"));
        Assert.Equal(30L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.accounts"));
        await migrator.MigrateAsync();
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.deduction_rules"));
        Assert.Equal(2L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.fee_profiles"));
        Assert.Equal(9L, await host.ScalarAsync<long>("SELECT count(*) FROM ledger.audit_events"));
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
