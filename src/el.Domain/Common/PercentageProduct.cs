using System.Numerics;

namespace el.Domain.Common;

/// <summary>
/// An exact, unreduced and unrounded product: the real value is
/// <c>Numerator / Denominator</c>. Rounding that fraction to minor units is a separate step.
/// </summary>
public sealed record PercentageProduct(BigInteger Numerator, BigInteger Denominator);
