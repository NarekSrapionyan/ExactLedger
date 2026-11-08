using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class RoundingPolicyTests
{
    [Theory]
    [InlineData(RoundingMode.HalfEven, "HalfEven")]
    [InlineData(RoundingMode.HalfAwayFromZero, "HalfAwayFromZero")]
    [InlineData(RoundingMode.TowardZero, "TowardZero")]
    public void For_exposes_the_mode_a_stable_name_and_the_version(RoundingMode mode, string name)
    {
        var policy = RoundingPolicy.For(mode);

        Assert.Equal(mode, policy.Mode);
        Assert.Equal(name, policy.Name);
        Assert.Equal(1, policy.Version);
        Assert.Equal(RoundingPolicy.CurrentVersion, policy.Version);
    }

    [Fact]
    public void Every_defined_mode_has_a_policy_with_a_unique_name()
    {
        var modes = Enum.GetValues<RoundingMode>();
        var names = modes.Select(mode => RoundingPolicy.For(mode).Name).ToList();

        Assert.Equal(3, modes.Length);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MaxValue)]
    public void For_rejects_modes_that_are_not_defined(int value)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => RoundingPolicy.For((RoundingMode)value));

        Assert.Equal("mode", exception.ParamName);
    }

    [Fact]
    public void The_default_mode_is_not_a_valid_mode()
    {
        Assert.Equal(0, (int)default(RoundingMode));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoundingPolicy.For(default));
    }

    [Fact]
    public void Equality_uses_mode_name_and_version()
    {
        var first = RoundingPolicy.For(RoundingMode.HalfEven);
        var second = RoundingPolicy.For(RoundingMode.HalfEven);

        var equalByOperator = first == second;

        Assert.Equal(first, second);
        Assert.True(equalByOperator);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, RoundingPolicy.For(RoundingMode.TowardZero));
    }

    [Fact]
    public void ToString_shows_the_name_and_version()
    {
        Assert.Equal("HalfEven v1", RoundingPolicy.For(RoundingMode.HalfEven).ToString());
    }
}
