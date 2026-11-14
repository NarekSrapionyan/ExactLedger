using System.Numerics;

namespace el.Domain.Common;

/// <summary>
/// Immutable percentage held as integer rate units: the percent multiplied by 1,000,000,
/// so 2.9% is 2,900,000 units. The range is 0% to 100% with at most six fractional digits.
/// A rate is not Money and carries no currency. Multiplying a base by it is exact
/// (<see cref="Multiply"/>); rounding is a separate, explicit step.
/// </summary>
public sealed record ScaledPercentage
{
    /// <summary>Largest number of fractional digits of a percentage.</summary>
    public const int FractionalDigits = 6;

    /// <summary>Rate units per one percent.</summary>
    public const long Scale = 1_000_000;

    /// <summary>
    /// Denominator of every product: 100 (percent) times <see cref="Scale"/>.
    /// A rate of <see cref="MaxRateUnits"/> over this denominator is exactly 1.
    /// </summary>
    public const long Denominator = 100 * Scale;

    /// <summary>Rate units of 100%.</summary>
    public const long MaxRateUnits = Denominator;

    private ScaledPercentage(long rateUnits)
    {
        RateUnits = rateUnits;
    }

    public long RateUnits { get; }

    /// <exception cref="ArgumentOutOfRangeException">The value is below 0% or above 100%.</exception>
    public static ScaledPercentage FromRateUnits(long rateUnits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rateUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rateUnits, MaxRateUnits);

        return new ScaledPercentage(rateUnits);
    }

    /// <summary>
    /// Exact product of a base in minor units and this rate: the numerator is
    /// <c>baseMinorUnits * RateUnits</c> and the denominator is <see cref="Denominator"/>.
    /// Nothing is divided, reduced or rounded. The product can exceed 64 bits even for
    /// a valid Money amount, so it is computed with BigInteger. The base may be negative;
    /// the sign stays on the numerator.
    /// </summary>
    public PercentageProduct Multiply(long baseMinorUnits) =>
        new(new BigInteger(baseMinorUnits) * RateUnits, Denominator);

    public override string ToString() =>
        FormattableString.Invariant($"{RateUnits} rate units");
}
