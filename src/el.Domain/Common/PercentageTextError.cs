namespace el.Domain.Common;

/// <summary>Why a percentage string was rejected.</summary>
public enum PercentageTextError
{
    None = 0,

    /// <summary>The text is null or empty.</summary>
    Empty,

    /// <summary>Anything outside <c>digits[.digits]</c>: sign, whitespace, '%', grouping, exponents, ".5", "5.", non-ASCII digits.</summary>
    InvalidFormat,

    /// <summary>The integer part has leading zeros, such as "02.9".</summary>
    LeadingZeros,

    /// <summary>More than six fractional digits, such as "2.9000001".</summary>
    ExcessPrecision,

    /// <summary>The percentage is above 100.</summary>
    OutOfRange,
}
