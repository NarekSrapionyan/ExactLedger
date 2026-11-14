namespace el.Api.Contracts;

/// <summary>Stable error codes reported when a JSON money value is rejected.</summary>
public static class MoneyJsonErrorCodes
{
    public const string NotObject = "money.not_object";
    public const string NumericNotAllowed = "money.numeric_not_allowed";
    public const string MissingAmount = "money.missing_amount";
    public const string MissingCurrency = "money.missing_currency";
    public const string AmountNotString = "money.amount_not_string";
    public const string InvalidCurrency = "money.invalid_currency";
    public const string UnknownCurrency = "money.unknown_currency";
    public const string UnknownProperty = "money.unknown_property";
    public const string DuplicateProperty = "money.duplicate_property";
    public const string AmountEmpty = "money.amount_empty";
    public const string AmountInvalidFormat = "money.amount_invalid_format";
    public const string AmountLeadingZeros = "money.amount_leading_zeros";
    public const string AmountNegativeZero = "money.amount_negative_zero";
    public const string AmountMissingPrecision = "money.amount_missing_precision";
    public const string AmountExcessPrecision = "money.amount_excess_precision";
    public const string AmountOutOfRange = "money.amount_out_of_range";
}
