using System.Numerics;

namespace el.Domain.Common;

/// <summary>
/// Explicit, exact rounding of a fraction (integer numerator over a positive integer
/// denominator) to a whole number of minor units. Every method requires the mode: there is
/// no default and no overload without one. Only integer quotient and remainder are used,
/// so nothing is approximated; a midpoint is detected by comparing twice the remainder with
/// the denominator.
/// </summary>
public static class Rounding
{
    /// <summary>Rounds the exact product from <see cref="ScaledPercentage.Multiply"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The denominator is not positive or the mode is not defined.</exception>
    public static RoundingResult Round(PercentageProduct product, RoundingMode mode)
    {
        ArgumentNullException.ThrowIfNull(product);

        return Round(product.Numerator, product.Denominator, mode);
    }

    /// <exception cref="ArgumentOutOfRangeException">The denominator is not positive or the mode is not defined.</exception>
    private static bool RoundsAwayFromZero(RoundingMode mode, BigInteger quotient, BigInteger remainder, BigInteger denominator)
    {
        // Twice the remainder against the denominator is an exact comparison with one half.
        var doubledRemainder = remainder * 2;

        return mode switch
        {
            RoundingMode.HalfEven => doubledRemainder > denominator
                || (doubledRemainder == denominator && !quotient.IsEven),
            RoundingMode.HalfAwayFromZero => doubledRemainder >= denominator,
            RoundingMode.TowardZero => false,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The rounding mode is not defined."),
        };
    }
    public static RoundingResult Round(BigInteger numerator, BigInteger denominator, RoundingMode mode)
    {
        if (denominator.Sign <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "The denominator must be positive.");
        }

        var negative = numerator.Sign < 0;
        var quotient = BigInteger.DivRem(BigInteger.Abs(numerator), denominator, out var remainder);
        // based on mode , the quotient may be incremented by one => check that in RoundsAwayFromZero
        var incremented = RoundsAwayFromZero(mode, quotient, remainder, denominator);
        var magnitude = incremented ? quotient + BigInteger.One : quotient;

        return new RoundingResult(
            numerator,
            denominator,
            RoundingPolicy.For(mode),
            quotient,
            remainder,
            incremented,
            negative ? -magnitude : magnitude);
    }

}
