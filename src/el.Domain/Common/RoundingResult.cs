using System.Numerics;

namespace el.Domain.Common;

/// <summary>
/// The exact input, working values and output of one rounding step, for calculation traces.
/// Only <see cref="Rounding"/> creates it, so the values are always consistent with each other.
/// The quotient and remainder belong to the absolute numerator (<c>|Numerator| = AbsoluteQuotient
/// * Denominator + AbsoluteRemainder</c>); the sign of <see cref="Numerator"/> is restored in
/// <see cref="Value"/>. This type holds no persistence or serialization concerns.
/// </summary>
public sealed record RoundingResult
{
    internal RoundingResult(
        BigInteger numerator,
        BigInteger denominator,
        RoundingPolicy policy,
        BigInteger absoluteQuotient,
        BigInteger absoluteRemainder,
        bool magnitudeIncremented,
        BigInteger value)
    {
        Numerator = numerator;
        Denominator = denominator;
        Policy = policy;
        AbsoluteQuotient = absoluteQuotient;
        AbsoluteRemainder = absoluteRemainder;
        MagnitudeIncremented = magnitudeIncremented;
        Value = value;
    }

    /// <summary>The exact numerator that was rounded; it may be negative.</summary>
    public BigInteger Numerator { get; }

    /// <summary>The exact denominator; always positive.</summary>
    public BigInteger Denominator { get; }

    public RoundingPolicy Policy { get; }

    public BigInteger AbsoluteQuotient { get; }

    public BigInteger AbsoluteRemainder { get; }

    /// <summary>True when the absolute quotient was increased by one.</summary>
    public bool MagnitudeIncremented { get; }

    /// <summary>The rounded whole number, with the sign of <see cref="Numerator"/> restored.</summary>
    public BigInteger Value { get; }

    /// <summary>
    /// Narrows the rounded value into Money. The range is checked before narrowing, so a value
    /// that does not fit even in 64 bits is rejected the same way as one just past the limit.
    /// </summary>
    /// <exception cref="OverflowException">The value is outside the Money range.</exception>
    public Money ToMoney(CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (Value < Money.MinMinorUnits || Value > Money.MaxMinorUnits)
        {
            throw new OverflowException(FormattableString.Invariant(
                $"The rounded {currency} amount is outside the Money range of {Money.MinMinorUnits} to {Money.MaxMinorUnits}."));
        }

        return Money.FromMinorUnits((long)Value, currency);
    }

    public override string ToString() =>
        FormattableString.Invariant($"{Numerator}/{Denominator} rounded {Policy} = {Value}");
}
