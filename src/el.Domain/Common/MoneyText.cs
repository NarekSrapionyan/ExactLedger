using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace el.Domain.Common;

/// <summary>
/// Exact, culture-independent conversion between Money and plain decimal strings.
/// The canonical form is <c>-?(0|[1-9][0-9]*)</c>, followed by '.' and exactly the
/// currency exponent's digits when the exponent is above zero: USD "250.00", JPY "1000",
/// KWD "1.234". Zero is never negative ("0.00", not "-0.00"). Parsing accepts only the
/// canonical form and never rounds.
/// </summary>
public static class MoneyText
{
    /// <exception cref="MoneyFormatException">The text is not a canonical amount for the currency.</exception>
    public static Money Parse(string text, CurrencyDefinition currency) =>
        TryParse(text, currency, out var money, out var error)
            ? money
            : throw new MoneyFormatException(error, currency);

    public static bool TryParse(
        [NotNullWhen(true)] string? text,
        CurrencyDefinition currency,
        [NotNullWhen(true)] out Money? money,
        out MoneyTextError error)
    {
        ArgumentNullException.ThrowIfNull(currency);

        money = null;
        error = Validate(text, currency.Exponent, out var negative, out var minorUnits);

        if (error != MoneyTextError.None)
        {
            return false;
        }

        money = Money.FromMinorUnits(negative ? -minorUnits : minorUnits, currency.Code);
        return true;
    }

    /// <exception cref="CurrencyMismatchException">The amount is not in <paramref name="currency"/>.</exception>
    public static string Format(Money money, CurrencyDefinition currency)
    {
        ArgumentNullException.ThrowIfNull(money);
        ArgumentNullException.ThrowIfNull(currency);

        if (money.Currency != currency.Code)
        {
            throw new CurrencyMismatchException(currency.Code, money.Currency);
        }

        // The Money range is symmetric, so the absolute value cannot overflow.
        var digits = Math.Abs(money.MinorUnits).ToString(CultureInfo.InvariantCulture);
        var sign = money.IsNegative ? "-" : string.Empty;

        if (currency.Exponent == 0)
        {
            return sign + digits;
        }

        digits = digits.PadLeft(currency.Exponent + 1, '0');
        var split = digits.Length - currency.Exponent;

        return sign + digits[..split] + "." + digits[split..];
    }

    private static MoneyTextError Validate(string? text, int exponent, out bool negative, out long minorUnits)
    {
        negative = false;
        minorUnits = 0;

        if (string.IsNullOrEmpty(text))
        {
            return MoneyTextError.Empty;
        }

        var position = 0;

        if (text[0] == '-')
        {
            negative = true;
            position = 1;
        }

        var integerStart = position;
        position = SkipDigits(text, position);
        var integerLength = position - integerStart;

        if (integerLength == 0)
        {
            return MoneyTextError.InvalidFormat;
        }

        var hasPoint = position < text.Length && text[position] == '.';
        var fractionStart = hasPoint ? position + 1 : position;
        position = hasPoint ? SkipDigits(text, fractionStart) : position;
        var fractionLength = position - fractionStart;

        if (position != text.Length || (hasPoint && fractionLength == 0))
        {
            return MoneyTextError.InvalidFormat;
        }

        if (integerLength > 1 && text[integerStart] == '0')
        {
            return MoneyTextError.LeadingZeros;
        }

        if (fractionLength > exponent)
        {
            return MoneyTextError.ExcessPrecision;
        }

        if (fractionLength < exponent)
        {
            return MoneyTextError.MissingPrecision;
        }

        // Integer part and fraction together are the minor units. Stop as soon as the
        // value passes the Money range, so very long input cannot overflow a long.
        for (var index = integerStart; index < text.Length; index++)
        {
            if (index == integerStart + integerLength)
            {
                continue;
            }

            minorUnits = (minorUnits * 10) + (text[index] - '0');

            if (minorUnits > Money.MaxMinorUnits)
            {
                return MoneyTextError.OutOfRange;
            }
        }

        if (negative && minorUnits == 0)
        {
            return MoneyTextError.NegativeZero;
        }

        return MoneyTextError.None;
    }

    private static int SkipDigits(string text, int position)
    {
        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            position++;
        }

        return position;
    }
}
