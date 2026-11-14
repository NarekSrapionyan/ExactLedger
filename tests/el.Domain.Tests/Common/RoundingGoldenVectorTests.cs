using System.Numerics;
using System.Reflection;
using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class RoundingGoldenVectorTests
{
    private static void AssertRounds(RoundingMode mode, long numerator, long denominator, long expected)
    {
        var result = Rounding.Round(numerator, denominator, mode);

        Assert.Equal(new BigInteger(expected), result.Value);
    }

    [Theory]
    // exact integers and zero
    [InlineData(0, 1, 0)]
    [InlineData(0, 100_000_000, 0)]
    [InlineData(5, 1, 5)]
    [InlineData(-5, 1, -5)]
    [InlineData(72_500_000_000, 100_000_000, 725)]
    [InlineData(-72_500_000_000, 100_000_000, -725)]
    [InlineData(100_000_000, 100_000_000, 1)]
    [InlineData(-300_000_000_000, 100_000_000, -3000)]
    // below the midpoint
    [InlineData(1, 3, 0)]
    [InlineData(-1, 3, 0)]
    [InlineData(2344, 10, 234)]
    [InlineData(-2344, 10, -234)]
    [InlineData(149_999_999, 100_000_000, 1)]
    [InlineData(-49_999_999, 100_000_000, 0)]
    // above the midpoint
    [InlineData(2, 3, 1)]
    [InlineData(-2, 3, -1)]
    [InlineData(2346, 10, 235)]
    [InlineData(-2346, 10, -235)]
    [InlineData(99_999_999, 100_000_000, 1)]
    [InlineData(-99_999_999, 100_000_000, -1)]
    [InlineData(150_000_001, 100_000_000, 2)]
    [InlineData(-150_000_001, 100_000_000, -2)]
    // midpoints with an even quotient: stays
    [InlineData(1, 2, 0)]
    [InlineData(-1, 2, 0)]
    [InlineData(5, 2, 2)]
    [InlineData(-5, 2, -2)]
    [InlineData(2345, 10, 234)]
    [InlineData(-2345, 10, -234)]
    [InlineData(250_000_000, 100_000_000, 2)]
    [InlineData(-50_000_000, 100_000_000, 0)]
    // midpoints with an odd quotient: moves to the even neighbour
    [InlineData(3, 2, 2)]
    [InlineData(-3, 2, -2)]
    [InlineData(7, 2, 4)]
    [InlineData(-7, 2, -4)]
    [InlineData(2355, 10, 236)]
    [InlineData(-2355, 10, -236)]
    [InlineData(204_950_000_000, 100_000_000, 2050)]
    [InlineData(199_950_000_000, 100_000_000, 2000)]
    [InlineData(-204_950_000_000, 100_000_000, -2050)]
    public void Half_even_matches_the_golden_vectors(long numerator, long denominator, long expected) =>
        AssertRounds(RoundingMode.HalfEven, numerator, denominator, expected);

    [Theory]
    // exact integers and zero
    [InlineData(0, 1, 0)]
    [InlineData(0, 100_000_000, 0)]
    [InlineData(5, 1, 5)]
    [InlineData(-5, 1, -5)]
    [InlineData(72_500_000_000, 100_000_000, 725)]
    [InlineData(-72_500_000_000, 100_000_000, -725)]
    [InlineData(100_000_000, 100_000_000, 1)]
    [InlineData(-300_000_000_000, 100_000_000, -3000)]
    // below the midpoint
    [InlineData(1, 3, 0)]
    [InlineData(-1, 3, 0)]
    [InlineData(2344, 10, 234)]
    [InlineData(-2344, 10, -234)]
    [InlineData(149_999_999, 100_000_000, 1)]
    [InlineData(-49_999_999, 100_000_000, 0)]
    // above the midpoint
    [InlineData(2, 3, 1)]
    [InlineData(-2, 3, -1)]
    [InlineData(2346, 10, 235)]
    [InlineData(-2346, 10, -235)]
    [InlineData(99_999_999, 100_000_000, 1)]
    [InlineData(-99_999_999, 100_000_000, -1)]
    [InlineData(150_000_001, 100_000_000, 2)]
    [InlineData(-150_000_001, 100_000_000, -2)]
    // midpoints with an even quotient: goes away from zero
    [InlineData(1, 2, 1)]
    [InlineData(-1, 2, -1)]
    [InlineData(5, 2, 3)]
    [InlineData(-5, 2, -3)]
    [InlineData(2345, 10, 235)]
    [InlineData(-2345, 10, -235)]
    [InlineData(250_000_000, 100_000_000, 3)]
    [InlineData(-50_000_000, 100_000_000, -1)]
    // midpoints with an odd quotient: goes away from zero
    [InlineData(3, 2, 2)]
    [InlineData(-3, 2, -2)]
    [InlineData(7, 2, 4)]
    [InlineData(-7, 2, -4)]
    [InlineData(2355, 10, 236)]
    [InlineData(-2355, 10, -236)]
    [InlineData(204_950_000_000, 100_000_000, 2050)]
    [InlineData(199_950_000_000, 100_000_000, 2000)]
    [InlineData(-204_950_000_000, 100_000_000, -2050)]
    public void Half_away_from_zero_matches_the_golden_vectors(long numerator, long denominator, long expected) =>
        AssertRounds(RoundingMode.HalfAwayFromZero, numerator, denominator, expected);

    [Theory]
    // exact integers and zero
    [InlineData(0, 1, 0)]
    [InlineData(0, 100_000_000, 0)]
    [InlineData(5, 1, 5)]
    [InlineData(-5, 1, -5)]
    [InlineData(72_500_000_000, 100_000_000, 725)]
    [InlineData(-72_500_000_000, 100_000_000, -725)]
    [InlineData(100_000_000, 100_000_000, 1)]
    [InlineData(-300_000_000_000, 100_000_000, -3000)]
    // below the midpoint
    [InlineData(1, 3, 0)]
    [InlineData(-1, 3, 0)]
    [InlineData(2344, 10, 234)]
    [InlineData(-2344, 10, -234)]
    [InlineData(149_999_999, 100_000_000, 1)]
    [InlineData(-49_999_999, 100_000_000, 0)]
    // above the midpoint: the fraction is still dropped
    [InlineData(2, 3, 0)]
    [InlineData(-2, 3, 0)]
    [InlineData(2346, 10, 234)]
    [InlineData(-2346, 10, -234)]
    [InlineData(99_999_999, 100_000_000, 0)]
    [InlineData(-99_999_999, 100_000_000, 0)]
    [InlineData(150_000_001, 100_000_000, 1)]
    [InlineData(-150_000_001, 100_000_000, -1)]
    // midpoints with an even quotient
    [InlineData(1, 2, 0)]
    [InlineData(-1, 2, 0)]
    [InlineData(5, 2, 2)]
    [InlineData(-5, 2, -2)]
    [InlineData(2345, 10, 234)]
    [InlineData(-2345, 10, -234)]
    [InlineData(250_000_000, 100_000_000, 2)]
    [InlineData(-50_000_000, 100_000_000, 0)]
    // midpoints with an odd quotient
    [InlineData(3, 2, 1)]
    [InlineData(-3, 2, -1)]
    [InlineData(7, 2, 3)]
    [InlineData(-7, 2, -3)]
    [InlineData(2355, 10, 235)]
    [InlineData(-2355, 10, -235)]
    [InlineData(204_950_000_000, 100_000_000, 2049)]
    [InlineData(199_950_000_000, 100_000_000, 1999)]
    [InlineData(-204_950_000_000, 100_000_000, -2049)]
    public void Toward_zero_matches_the_golden_vectors(long numerator, long denominator, long expected) =>
        AssertRounds(RoundingMode.TowardZero, numerator, denominator, expected);

    [Theory]
    [InlineData(nameof(Half_even_matches_the_golden_vectors))]
    [InlineData(nameof(Half_away_from_zero_matches_the_golden_vectors))]
    [InlineData(nameof(Toward_zero_matches_the_golden_vectors))]
    public void Each_mode_has_at_least_30_golden_vectors(string testName)
    {
        var method = typeof(RoundingGoldenVectorTests).GetMethod(testName);

        Assert.NotNull(method);
        Assert.True(method.GetCustomAttributes<InlineDataAttribute>().Count() >= 30);
    }
}
