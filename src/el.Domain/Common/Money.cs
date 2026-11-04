using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace el.Domain.Common;

/// <summary>
/// Immutable signed amount in integer minor units of one currency (USD 250.00 is 25000).
/// Equality uses amount and currency. Arithmetic and ordered comparison require the same
/// currency; there is no implicit conversion. Zero and negative values are valid Money,
/// so commands that need a positive amount must validate that separately.
/// </summary>
public sealed record Money : IComparable<Money>
{
    /// <summary>Largest absolute value of a single Money, in minor units.</summary>
    public const long MaxMinorUnits = 99_999_999_999;

    public const long MinMinorUnits = -MaxMinorUnits;

    private Money(long minorUnits, CurrencyCode currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    public long MinorUnits { get; }

    public CurrencyCode Currency { get; }

    public bool IsZero => MinorUnits == 0;

    public bool IsPositive => MinorUnits > 0;

    public bool IsNegative => MinorUnits < 0;

    public static Money FromMinorUnits(long minorUnits, CurrencyCode currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentOutOfRangeException.ThrowIfLessThan(minorUnits, MinMinorUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minorUnits, MaxMinorUnits);

        return new Money(minorUnits, currency);
    }

    public static Money Zero(CurrencyCode currency) => FromMinorUnits(0, currency);

    /// <exception cref="CurrencyMismatchException">The currencies differ.</exception>
    /// <exception cref="OverflowException">The result is outside the Money range.</exception>
    public Money Add(Money other)
    {
        EnsureSameCurrency(other);

        return WithMinorUnits(checked(MinorUnits + other.MinorUnits));
    }

    /// <exception cref="CurrencyMismatchException">The currencies differ.</exception>
    /// <exception cref="OverflowException">The result is outside the Money range.</exception>
    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);

        return WithMinorUnits(checked(MinorUnits - other.MinorUnits));
    }

    /// <summary>The range is symmetric, so negation never leaves it.</summary>
    public Money Negate() => new(-MinorUnits, Currency);

    /// <exception cref="CurrencyMismatchException">The currencies differ.</exception>
    public int CompareTo(Money? other)
    {
        EnsureSameCurrency(other);

        return MinorUnits.CompareTo(other.MinorUnits);
    }

    public override string ToString() =>
        FormattableString.Invariant($"{MinorUnits} {Currency} minor units");

    public static Money operator +(Money left, Money right) => NotNull(left).Add(right);

    public static Money operator -(Money left, Money right) => NotNull(left).Subtract(right);

    public static Money operator -(Money value) => NotNull(value).Negate();

    public static bool operator <(Money left, Money right) => NotNull(left).CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => NotNull(left).CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => NotNull(left).CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => NotNull(left).CompareTo(right) >= 0;

    private void EnsureSameCurrency([NotNull] Money? other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Currency != Currency)
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }

    private Money WithMinorUnits(long minorUnits) =>
        minorUnits is < MinMinorUnits or > MaxMinorUnits
            ? throw new OverflowException(FormattableString.Invariant(
                $"Result of {minorUnits} {Currency} minor units is outside the Money range of {MinMinorUnits} to {MaxMinorUnits}."))
            : new Money(minorUnits, Currency);

    private static Money NotNull(Money value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        return value;
    }
}
