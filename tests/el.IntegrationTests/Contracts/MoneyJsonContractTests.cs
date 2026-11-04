using System.Text.Json;
using System.Text.Json.Nodes;
using el.Api.Contracts;
using el.Domain.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace el.IntegrationTests.Contracts;

public sealed class MoneyJsonContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new MoneyJsonConverter(CurrencyReferenceData.Catalog, CurrencyReferenceData.InitialVersion) },
    };

    public static TheoryData<string, long> ValidFixtures
    {
        get
        {
            var data = new TheoryData<string, long>();

            foreach (var fixture in LoadFixture("valid-money.json"))
            {
                data.Add(fixture.GetProperty("json").GetRawText(), fixture.GetProperty("minorUnits").GetInt64());
            }

            return data;
        }
    }

    public static TheoryData<string, string> InvalidFixtures
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var fixture in LoadFixture("invalid-money.json"))
            {
                data.Add(fixture.GetProperty("json").GetRawText(), fixture.GetProperty("code").GetString()!);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Valid_fixtures_read_exactly_and_write_back_unchanged(string json, long minorUnits)
    {
        var money = JsonSerializer.Deserialize<Money>(json, Options);

        Assert.NotNull(money);
        Assert.Equal(minorUnits, money.MinorUnits);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(JsonSerializer.Serialize(money, Options))));
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void Invalid_fixtures_are_rejected_with_their_error_code(string json, string code)
    {
        var exception = Assert.Throws<MoneyJsonException>(() => JsonSerializer.Deserialize<Money>(json, Options));

        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public void Fixtures_cover_every_error_code()
    {
        var documented = typeof(MoneyJsonErrorCodes)
            .GetFields()
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);

        var covered = LoadFixture("invalid-money.json")
            .Select(fixture => fixture.GetProperty("code").GetString()!)
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(documented, covered);
    }

    [Fact]
    public void Writes_the_canonical_string_amount()
    {
        var json = JsonSerializer.Serialize(Money.FromMinorUnits(-25000, CurrencyReferenceData.Usd), Options);

        Assert.Equal("""{"amount":"-250.00","currency":"USD"}""", json);
    }

    [Fact]
    public void Writing_an_unsupported_currency_fails()
    {
        var eur = Money.FromMinorUnits(100, CurrencyCode.Parse("EUR"));

        Assert.Throws<UnknownCurrencyException>(() => JsonSerializer.Serialize(eur, Options));
    }

    [Fact]
    public void Errors_inside_a_request_report_the_property_path()
    {
        var exception = Assert.Throws<MoneyJsonException>(() => JsonSerializer.Deserialize<PriceRequest>(
            """{"price":{"amount":250.00,"currency":"USD"}}""",
            Options));

        Assert.Equal(MoneyJsonErrorCodes.NumericNotAllowed, exception.Code);
        Assert.NotNull(exception.Path);
        Assert.StartsWith("$.price", exception.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_money_inside_a_request_is_rejected_with_its_property_path()
    {
        var exception = Assert.Throws<MoneyJsonException>(() => JsonSerializer.Deserialize<PriceRequest>(
            """{"price":null}""", Options));

        Assert.Equal(MoneyJsonErrorCodes.NotObject, exception.Code);
        Assert.Equal("$.price", exception.Path);
    }

    [Fact]
    public void Null_money_inside_an_array_is_rejected_with_its_element_path()
    {
        var exception = Assert.Throws<MoneyJsonException>(() => JsonSerializer.Deserialize<Money[]>("[null]", Options));

        Assert.Equal(MoneyJsonErrorCodes.NotObject, exception.Code);
        Assert.Equal("$[0]", exception.Path);
    }

    [Fact]
    public void Writing_null_money_is_rejected_instead_of_silently_emitting_null()
    {
        var root = Assert.Throws<MoneyJsonException>(() => JsonSerializer.Serialize<Money>(null!, Options));
        var nested = Assert.Throws<MoneyJsonException>(() => JsonSerializer.Serialize(new PriceRequest(null!), Options));

        Assert.Equal(MoneyJsonErrorCodes.NotObject, root.Code);
        Assert.Equal(MoneyJsonErrorCodes.NotObject, nested.Code);
    }

    [Fact]
    public void Money_inside_a_request_round_trips()
    {
        var request = JsonSerializer.Deserialize<PriceRequest>(
            """{"price":{"amount":"1.234","currency":"KWD"}}""",
            Options);

        Assert.NotNull(request);
        Assert.Equal(Money.FromMinorUnits(1234, CurrencyReferenceData.Kwd), request.Price);
        Assert.Equal("""{"price":{"amount":"1.234","currency":"KWD"}}""", JsonSerializer.Serialize(request, Options));
    }

    [Fact]
    public async Task Api_json_options_use_the_money_contract()
    {
        await using var factory = new WebApplicationFactory<Program>();

        var options = factory.Services.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

        Assert.Equal(
            """{"amount":"250.00","currency":"USD"}""",
            JsonSerializer.Serialize(Money.FromMinorUnits(25000, CurrencyReferenceData.Usd), options));
        Assert.Throws<MoneyJsonException>(
            () => JsonSerializer.Deserialize<Money>("""{"amount":250.00,"currency":"USD"}""", options));
        Assert.Throws<MoneyJsonException>(() => JsonSerializer.Deserialize<Money>("null", options));
        Assert.Throws<MoneyJsonException>(() => JsonSerializer.Deserialize<PriceRequest>("""{"price":null}""", options));
    }

    public sealed record PriceRequest(Money Price);

    private static JsonElement[] LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Money", fileName);

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }
}
