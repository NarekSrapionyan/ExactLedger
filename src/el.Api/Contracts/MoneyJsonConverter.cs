using System.Text.Json;
using System.Text.Json.Serialization;
using el.Domain.Common;

namespace el.Api.Contracts;

/// <summary>
/// JSON contract for Money: <c>{"amount":"250.00","currency":"USD"}</c>. The amount is always a
/// canonical decimal string for the currency (see MoneyText); JSON numbers are rejected so no
/// client or serializer ever passes money through a binary floating-point value.
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    private const string AmountProperty = "amount";
    private const string CurrencyProperty = "currency";

    private readonly CurrencyCatalog _catalog;
    private readonly int _referenceVersion;

    // Reference converters otherwise let System.Text.Json bypass Read/Write for null.
    public override bool HandleNull => true;

    public MoneyJsonConverter(CurrencyCatalog catalog, int referenceVersion)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _catalog = catalog;
        _referenceVersion = referenceVersion;
    }

    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            throw Error(MoneyJsonErrorCodes.NumericNotAllowed, "Money must be an object with a string amount, not a JSON number.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw Error(MoneyJsonErrorCodes.NotObject, "Money must be an object with \"amount\" and \"currency\" properties.");
        }

        string? amount = null;
        string? currency = null;
        var hasAmount = false;
        var hasCurrency = false;

        while (ReadNext(ref reader) != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            ReadNext(ref reader);

            switch (name)
            {
                case AmountProperty:
                    EnsureFirst(ref hasAmount, AmountProperty);
                    amount = reader.TokenType switch
                    {
                        JsonTokenType.String => reader.GetString(),
                        JsonTokenType.Number => throw Error(MoneyJsonErrorCodes.NumericNotAllowed, "Money \"amount\" must be a decimal string such as \"250.00\", not a JSON number."),
                        _ => throw Error(MoneyJsonErrorCodes.AmountNotString, "Money \"amount\" must be a decimal string such as \"250.00\"."),
                    };
                    break;

                case CurrencyProperty:
                    EnsureFirst(ref hasCurrency, CurrencyProperty);
                    currency = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : throw Error(MoneyJsonErrorCodes.InvalidCurrency, "Money \"currency\" must be a three-letter uppercase code such as \"USD\".");
                    break;

                default:
                    throw Error(MoneyJsonErrorCodes.UnknownProperty, "Money only allows the \"amount\" and \"currency\" properties.");
            }
        }

        if (!hasAmount)
        {
            throw Error(MoneyJsonErrorCodes.MissingAmount, "Money \"amount\" is required.");
        }

        if (!hasCurrency)
        {
            throw Error(MoneyJsonErrorCodes.MissingCurrency, "Money \"currency\" is required.");
        }

        if (!CurrencyCode.TryParse(currency, out var code))
        {
            throw Error(MoneyJsonErrorCodes.InvalidCurrency, "Money \"currency\" must be a three-letter uppercase code such as \"USD\".");
        }

        if (!_catalog.TryResolve(code, _referenceVersion, out var definition))
        {
            throw Error(MoneyJsonErrorCodes.UnknownCurrency, $"Currency {code} is not supported.");
        }

        if (!MoneyText.TryParse(amount, definition, out var money, out var error))
        {
            var formatException = new MoneyFormatException(error, definition);
            throw new MoneyJsonException(CodeFor(error), formatException.Message, formatException);
        }

        return money;
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            throw Error(MoneyJsonErrorCodes.NotObject, "Money must be an object with amount and currency, not null.");
        }

        var definition = _catalog.Resolve(value.Currency, _referenceVersion);

        writer.WriteStartObject();
        writer.WriteString(AmountProperty, MoneyText.Format(value, definition));
        writer.WriteString(CurrencyProperty, value.Currency.Value);
        writer.WriteEndObject();
    }

    private static JsonTokenType ReadNext(ref Utf8JsonReader reader) =>
        reader.Read()
            ? reader.TokenType
            : throw Error(MoneyJsonErrorCodes.NotObject, "Money object is incomplete.");

    private static void EnsureFirst(ref bool seen, string property)
    {
        if (seen)
        {
            throw Error(MoneyJsonErrorCodes.DuplicateProperty, $"Money \"{property}\" must appear only once.");
        }

        seen = true;
    }

    private static string CodeFor(MoneyTextError error) => error switch
    {
        MoneyTextError.Empty => MoneyJsonErrorCodes.AmountEmpty,
        MoneyTextError.LeadingZeros => MoneyJsonErrorCodes.AmountLeadingZeros,
        MoneyTextError.NegativeZero => MoneyJsonErrorCodes.AmountNegativeZero,
        MoneyTextError.MissingPrecision => MoneyJsonErrorCodes.AmountMissingPrecision,
        MoneyTextError.ExcessPrecision => MoneyJsonErrorCodes.AmountExcessPrecision,
        MoneyTextError.OutOfRange => MoneyJsonErrorCodes.AmountOutOfRange,
        _ => MoneyJsonErrorCodes.AmountInvalidFormat,
    };

    private static MoneyJsonException Error(string code, string message) => new(code, message);
}
