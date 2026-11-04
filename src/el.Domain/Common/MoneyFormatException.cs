namespace el.Domain.Common;

/// <summary>
/// Thrown when text is not a canonical amount for the currency. The input itself is not
/// included in the message.
/// </summary>
public sealed class MoneyFormatException : FormatException
{
    public MoneyFormatException(MoneyTextError error, CurrencyDefinition currency)
        : base(Describe(error, currency))
    {
        Error = error;
        Currency = currency.Code;
    }

    public MoneyTextError Error { get; }

    public CurrencyCode Currency { get; }

    private static string Describe(MoneyTextError error, CurrencyDefinition currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        return error switch
        {
            MoneyTextError.Empty => FormattableString.Invariant($"A {currency.Code} amount is required."),
            MoneyTextError.LeadingZeros => FormattableString.Invariant($"A {currency.Code} amount must not have leading zeros."),
            MoneyTextError.NegativeZero => FormattableString.Invariant($"A {currency.Code} zero amount must not be negative."),
            MoneyTextError.MissingPrecision or MoneyTextError.ExcessPrecision => currency.Exponent == 0
                ? FormattableString.Invariant($"A {currency.Code} amount must not have a decimal part.")
                : FormattableString.Invariant($"A {currency.Code} amount must have exactly {currency.Exponent} decimal places."),
            MoneyTextError.OutOfRange => FormattableString.Invariant($"The {currency.Code} amount is outside the supported range."),
            _ => FormattableString.Invariant($"A {currency.Code} amount must be a plain decimal such as {MoneyText.Format(Money.Zero(currency.Code), currency)}."),
        };
    }
}
