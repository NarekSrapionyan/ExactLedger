using System.Diagnostics.CodeAnalysis;

namespace el.Domain.Common;

/// <summary>
/// Alphabetic currency code: exactly three uppercase ASCII letters (A-Z).
/// Input is never normalized, so "usd" or " USD" is rejected rather than corrected.
/// </summary>
public sealed record CurrencyCode
{
    public const int Length = 3;

    private CurrencyCode(string value) => Value = value;

    public string Value { get; }

    public static CurrencyCode Parse(string value) =>
        TryParse(value, out var code)
            ? code
            : throw new ArgumentException(
                "Currency code must be exactly three uppercase ASCII letters (A-Z).",
                nameof(value));

    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out CurrencyCode? code)
    {
        if (value is not null && value.Length == Length && value.All(char.IsAsciiLetterUpper))
        {
            code = new CurrencyCode(value);
            return true;
        }

        code = null;
        return false;
    }

    public override string ToString() => Value;
}
