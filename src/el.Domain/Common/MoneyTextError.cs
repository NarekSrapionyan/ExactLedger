namespace el.Domain.Common;

/// <summary>Why a money string was rejected.</summary>
public enum MoneyTextError
{
    None = 0,

    /// <summary>The text is null or empty.</summary>
    Empty,

    /// <summary>Anything outside <c>-?digits[.digits]</c>: whitespace, '+', grouping, exponents, non-ASCII digits.</summary>
    InvalidFormat,

    /// <summary>The integer part has leading zeros, such as "01.00".</summary>
    LeadingZeros,

    /// <summary>A negative sign on zero, such as "-0.00".</summary>
    NegativeZero,

    /// <summary>Fewer fractional digits than the currency exponent, such as USD "250" or "250.0".</summary>
    MissingPrecision,

    /// <summary>More fractional digits than the currency exponent, such as USD "2.345" or JPY "1000.0".</summary>
    ExcessPrecision,

    /// <summary>The amount is outside the Money range.</summary>
    OutOfRange,
}
