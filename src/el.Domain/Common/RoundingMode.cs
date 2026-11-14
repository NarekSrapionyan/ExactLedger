namespace el.Domain.Common;

/// <summary>
/// How an exact fraction is rounded to a whole number of minor units. There is deliberately
/// no default: <c>default(RoundingMode)</c> (0) is not a valid mode, so a caller that forgets
/// to choose one is rejected instead of silently receiving a policy. Persist the policy name
/// (<see cref="RoundingPolicy.Name"/>), never the numeric value.
/// </summary>
public enum RoundingMode
{
    /// <summary>
    /// The nearest whole number; an exact midpoint goes to the even neighbour
    /// (2.5 becomes 2, 3.5 becomes 4, -2.5 becomes -2).
    /// </summary>
    HalfEven = 1,

    /// <summary>
    /// The nearest whole number; an exact midpoint goes away from zero
    /// (2.5 becomes 3, -2.5 becomes -3).
    /// </summary>
    HalfAwayFromZero = 2,

    /// <summary>The fraction is dropped (2.9 becomes 2, -2.9 becomes -2).</summary>
    TowardZero = 3,
}
