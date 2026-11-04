using System.Text.Json;

namespace el.Api.Contracts;

/// <summary>
/// A rejected JSON money value. <see cref="Code"/> is one of <see cref="MoneyJsonErrorCodes"/>;
/// the serializer fills <see cref="JsonException.Path"/> with the location of the value.
/// </summary>
public sealed class MoneyJsonException : JsonException
{
    public MoneyJsonException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
