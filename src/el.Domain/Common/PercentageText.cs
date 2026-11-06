using System.Diagnostics.CodeAnalysis;

namespace el.Domain.Common;

/// <summary>
/// Exact, culture-independent parsing of percentage text into a <see cref="ScaledPercentage"/>.
/// The accepted form is <c>(0|[1-9][0-9]*)</c> optionally followed by '.' and one to six digits:
/// "2.9", "2.900000", "100". Fewer than six fractional digits are fine, because the missing
/// digits are zeros. No sign, whitespace, '%', grouping separator or exponent is accepted, and
/// parsing never rounds.
/// </summary>
public static class PercentageText
{
    public static bool TryParse(
        [NotNullWhen(true)] string? text,
        [NotNullWhen(true)] out ScaledPercentage? percentage,
        out PercentageTextError error)
    {
        percentage = null;
        error = Validate(text, out var rateUnits);

        if (error != PercentageTextError.None)
        {
            return false;
        }

        percentage = ScaledPercentage.FromRateUnits(rateUnits);
        return true;
    }

    private static PercentageTextError Validate(string? text, out long rateUnits)
    {
        rateUnits = 0;

        if (string.IsNullOrEmpty(text))
        {
            return PercentageTextError.Empty;
        }

        var integerLength = SkipDigits(text, 0);

        if (integerLength == 0)
        {
            return PercentageTextError.InvalidFormat;
        }

        var position = integerLength;
        var hasPoint = position < text.Length && text[position] == '.';
        var fractionStart = hasPoint ? position + 1 : position;
        position = hasPoint ? SkipDigits(text, fractionStart) : position;
        var fractionLength = position - fractionStart;

        if (position != text.Length || (hasPoint && fractionLength == 0))
        {
            return PercentageTextError.InvalidFormat;
        }

        if (integerLength > 1 && text[0] == '0')
        {
            return PercentageTextError.LeadingZeros;
        }

        if (fractionLength > ScaledPercentage.FractionalDigits)
        {
            return PercentageTextError.ExcessPrecision;
        }

        // 100 has three digits, so anything longer is out of range. This also keeps
        // the arithmetic below far away from the limits of a long.
        if (integerLength > 3)
        {
            return PercentageTextError.OutOfRange;
        }

        long whole = 0;

        for (var index = 0; index < integerLength; index++)
        {
            whole = (whole * 10) + (text[index] - '0');
        }

        // Pad a short fraction with zeros: "9" becomes 900000.
        long fraction = 0;

        for (var index = 0; index < ScaledPercentage.FractionalDigits; index++)
        {
            var digit = index < fractionLength ? text[fractionStart + index] - '0' : 0;
            fraction = (fraction * 10) + digit;
        }

        rateUnits = (whole * ScaledPercentage.Scale) + fraction;

        return rateUnits > ScaledPercentage.MaxRateUnits
            ? PercentageTextError.OutOfRange
            : PercentageTextError.None;
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
