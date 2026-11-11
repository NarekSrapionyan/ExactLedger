using System.Diagnostics.CodeAnalysis;

namespace el.Domain.Ledger;

/// <summary>
/// Immutable human-readable account code such as "PAYABLE.SELLER-001.USD": 1 to 64 characters
/// of uppercase ASCII letters, digits, '.', '-' and '_'. Input is never normalized.
/// </summary>
public sealed record AccountCode
{
    public const int MaxLength = 64;

    private AccountCode(string value) => Value = value;

    public string Value { get; }

    public static AccountCode Parse(string value) =>
        TryParse(value, out var code)
            ? code
            : throw new ArgumentException(
                "Account code must be 1 to 64 characters of A-Z, 0-9, '.', '-' or '_'.",
                nameof(value));

    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out AccountCode? code)
    {
        if (value is not null && value.Length is > 0 and <= MaxLength && value.All(IsAllowed))
        {
            code = new AccountCode(value);
            return true;
        }

        code = null;
        return false;
    }

    public override string ToString() => Value;

    private static bool IsAllowed(char character) =>
        char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character is '.' or '-' or '_';
}
