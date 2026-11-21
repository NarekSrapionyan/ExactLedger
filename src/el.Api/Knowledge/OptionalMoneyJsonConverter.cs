using System.Text.Json;
using System.Text.Json.Serialization;
using el.Domain.Common;

namespace el.Api.Knowledge;

// Only optional floor/cap fields may be null. Non-null amounts retain the strict shared contract.
public sealed class OptionalMoneyJsonConverter : JsonConverter<Money>
{
    public override Money? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ((JsonConverter<Money>)options.GetConverter(typeof(Money))).Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
        ((JsonConverter<Money>)options.GetConverter(typeof(Money))).Write(writer, value, options);
}
