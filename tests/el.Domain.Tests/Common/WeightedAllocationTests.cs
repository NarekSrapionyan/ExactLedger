using System.Globalization;
using System.Numerics;
using el.Domain.Common;

namespace el.Domain.Tests.Common;

public sealed class WeightedAllocationTests
{
    public static TheoryData<long, long[], long[]> GoldenCases => new()
    {
        { 5L, new long[] { 50, 30, 20 }, new long[] { 3, 1, 1 } },
        { 0L, new long[] { 50, 30, 20 }, new long[] { 0, 0, 0 } },
        { 100L, new long[] { 50, 30, 20 }, new long[] { 50, 30, 20 } },
        { 1L, new long[] { 1, 1, 1 }, new long[] { 1, 0, 0 } },
        { 2L, new long[] { 1, 1, 1 }, new long[] { 1, 1, 0 } },
        { 10L, new long[] { 1, 1, 1 }, new long[] { 4, 3, 3 } },
        { 1L, new long[] { 0, 1, 1 }, new long[] { 0, 1, 0 } },
        { 5L, new long[] { 0, 0, 7 }, new long[] { 0, 0, 5 } },
        { long.MaxValue, new long[] { long.MaxValue }, new long[] { long.MaxValue } },
        { long.MaxValue, new long[] { long.MaxValue, long.MaxValue }, new long[] { 4_611_686_018_427_387_904, 4_611_686_018_427_387_903 } },
        { 5L, new long[] { long.MaxValue, long.MaxValue, long.MaxValue }, new long[] { 2, 2, 1 } },
    };

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void Matches_exact_golden_allocations(long amount, long[] weights, long[] expected)
    {
        var lines = weights.Select((weight, index) => new AllocationLine(FormattableString.Invariant($"line-{index:D2}"), weight));

        var result = WeightedAllocation.Allocate(amount, lines);

        Assert.Equal(expected, result.Select(share => share.MinorUnits));
    }

    [Fact]
    public void Higher_remainder_wins_before_identifier_order()
    {
        var result = WeightedAllocation.Allocate(1, [new("A", 1), new("B", 2)]);

        Assert.Equal(new long[] { 0, 1 }, result.Select(share => share.MinorUnits));
    }

    [Fact]
    public void Identifiers_are_case_sensitive_and_equal_remainders_use_ordinal_order()
    {
        var result = WeightedAllocation.Allocate(2, [new("a", 1), new("Z", 1), new("A", 1), new("ä", 1)]);

        Assert.Equal(new[] { "A", "Z", "a", "ä" }, result.Select(share => share.AllocationLineId));
        Assert.Equal(new long[] { 1, 1, 0, 0 }, result.Select(share => share.MinorUnits));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("hy-AM")]
    public void Allocation_does_not_depend_on_current_culture(string cultureName)
    {
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;

            var result = WeightedAllocation.Allocate(2, [new("ä", 1), new("a", 1), new("Z", 1), new("A", 1)]);

            Assert.Equal(new[] { "A", "Z", "a", "ä" }, result.Select(share => share.AllocationLineId));
            Assert.Equal(new long[] { 1, 1, 0, 0 }, result.Select(share => share.MinorUnits));
        }
        finally
        {
            CultureInfo.CurrentCulture = original.CurrentCulture;
            CultureInfo.CurrentUICulture = original.CurrentUICulture;
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Rejects_negative_amounts(long amount)
    {
        Assert.Equal("amountMinorUnits", Assert.Throws<ArgumentOutOfRangeException>(
            () => WeightedAllocation.Allocate(amount, [new("A", 1)])).ParamName);
    }

    [Fact]
    public void Rejects_null_sequence()
    {
        Assert.Equal("lines", Assert.Throws<ArgumentNullException>(() => WeightedAllocation.Allocate(1, null!)).ParamName);
    }

    [Fact]
    public void Rejects_null_entries()
    {
        Assert.Equal("lines", Assert.Throws<ArgumentException>(
            () => WeightedAllocation.Allocate(1, [new("A", 1), null!])).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Rejects_empty_or_all_zero_weights_even_for_zero_amount(long amount)
    {
        Assert.Equal("lines", Assert.Throws<ArgumentException>(() => WeightedAllocation.Allocate(amount, [])).ParamName);
        Assert.Equal("lines", Assert.Throws<ArgumentException>(
            () => WeightedAllocation.Allocate(amount, [new("A", 0), new("B", 0)])).ParamName);
    }

    [Fact]
    public void Rejects_duplicate_identifiers_including_zero_weight_lines()
    {
        Assert.Equal("lines", Assert.Throws<ArgumentException>(
            () => WeightedAllocation.Allocate(5, [new("A", 1), new("A", 0)])).ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Line_rejects_negative_weight(long weight)
    {
        Assert.Equal("weight", Assert.Throws<ArgumentOutOfRangeException>(() => new AllocationLine("A", weight)).ParamName);
    }

    [Fact]
    public void Line_rejects_null_identifier()
    {
        Assert.Equal("allocationLineId", Assert.Throws<ArgumentNullException>(() => new AllocationLine(null!, 1)).ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void Line_rejects_blank_identifier(string identifier)
    {
        Assert.Equal("allocationLineId", Assert.Throws<ArgumentException>(() => new AllocationLine(identifier, 1)).ParamName);
    }

    [Fact]
    public void Identifiers_are_preserved_without_trimming_or_normalization()
    {
        var result = WeightedAllocation.Allocate(2, [new(" A", 1), new("A", 1)]);

        Assert.Equal(new[] { " A", "A" }, result.Select(share => share.AllocationLineId));
    }

    [Fact]
    public void Enumerates_input_once_and_does_not_mutate_the_input_order()
    {
        var source = new AllocationLine[] { new("B", 1), new("A", 1) };
        var enumerations = 0;

        IEnumerable<AllocationLine> Once()
        {
            Assert.Equal(1, ++enumerations);

            foreach (var line in source)
            {
                yield return line;
            }
        }

        var result = WeightedAllocation.Allocate(1, Once());

        Assert.Equal(new[] { "B", "A" }, source.Select(line => line.AllocationLineId));
        Assert.Equal(new[] { "A", "B" }, result.Select(share => share.AllocationLineId));
    }

    [Fact]
    public void Result_is_a_read_only_snapshot()
    {
        var source = new AllocationLine[] { new("A", 1) };
        var result = WeightedAllocation.Allocate(5, source);

        source[0] = new AllocationLine("B", 2);

        Assert.Equal("A", result[0].AllocationLineId);
        Assert.Equal(5, result[0].MinorUnits);
        var list = Assert.IsAssignableFrom<IList<AllocationShare>>(result);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact]
    public void Increasing_total_can_reduce_a_share_so_cumulative_differences_are_not_refunds()
    {
        // The Alabama paradox is intrinsic to Largest Remainder, not an accounting reversal.
        var weights = new long[] { 1500, 1500, 900, 500, 500, 200 };
        var lines = weights.Select((weight, index) => new AllocationLine(FormattableString.Invariant($"line-{index}"), weight)).ToArray();

        var first = WeightedAllocation.Allocate(25, lines);
        var second = WeightedAllocation.Allocate(26, lines);

        Assert.Equal(new long[] { 7, 7, 4, 3, 3, 1 }, first.Select(share => share.MinorUnits));
        Assert.Equal(new long[] { 8, 8, 5, 2, 2, 1 }, second.Select(share => share.MinorUnits));
    }

    [Fact]
    public void Ten_thousand_cases_conserve_amount_and_have_exact_share_error_below_one_unit()
    {
        var random = new Random(20261108);

        for (var sample = 0; sample < 10_000; sample++)
        {
            var amount = (sample % 4) switch
            {
                0 => 0L,
                1 => long.MaxValue,
                2 => random.NextInt64(0, 20),
                _ => random.NextInt64(0, long.MaxValue),
            };
            var lines = Enumerable.Range(0, random.Next(1, 17))
                .Select(index => new AllocationLine(FormattableString.Invariant($"line-{index:D2}"),
                    index == 0 ? long.MaxValue : random.NextInt64(0, long.MaxValue)))
                .ToArray();

            // Exercise zero weights and many equal remainders as well as large random weights.
            if (sample % 3 == 0)
            {
                lines = lines.Select((line, index) => new AllocationLine(line.AllocationLineId, index % 2 == 0 ? 1 : 0)).ToArray();
            }

            var result = WeightedAllocation.Allocate(amount, lines);
            var repeated = WeightedAllocation.Allocate(amount, lines);
            random.Shuffle(lines);
            var shuffled = WeightedAllocation.Allocate(amount, lines);
            var weights = lines.ToDictionary(line => line.AllocationLineId, line => line.Weight, StringComparer.Ordinal);
            var totalWeight = lines.Aggregate(BigInteger.Zero, (total, line) => total + line.Weight);

            Assert.Equal(lines.Length, result.Count);
            Assert.Equal(result.ToArray(), repeated.ToArray());
            Assert.Equal(result.ToArray(), shuffled.ToArray());
            Assert.Equal(new BigInteger(amount), result.Aggregate(BigInteger.Zero, (total, share) => total + share.MinorUnits));

            foreach (var share in result)
            {
                Assert.InRange(share.MinorUnits, 0, amount);
                var weight = weights[share.AllocationLineId];
                var exactNumerator = new BigInteger(amount) * weight;

                // |allocated - exact share| < 1, compared without division or approximation.
                Assert.True(BigInteger.Abs((new BigInteger(share.MinorUnits) * totalWeight) - exactNumerator) < totalWeight);

                if (weight == 0)
                {
                    Assert.Equal(0, share.MinorUnits);
                }
            }

            // Largest Remainder: each share is its floor or floor + 1, and the lines rounded up are
            // exactly the first ones by remainder descending, then allocationLineId ordinal ascending.
            var ranked = result
                .Select(share =>
                {
                    var floor = BigInteger.DivRem(new BigInteger(amount) * weights[share.AllocationLineId], totalWeight, out var remainder);
                    var roundedUp = new BigInteger(share.MinorUnits) - floor;

                    Assert.True(roundedUp.IsZero || roundedUp.IsOne);

                    return (share.AllocationLineId, Remainder: remainder, RoundedUp: roundedUp.IsOne);
                })
                .OrderByDescending(line => line.Remainder)
                .ThenBy(line => line.AllocationLineId, StringComparer.Ordinal)
                .ToArray();
            var roundedUpCount = ranked.Count(line => line.RoundedUp);

            Assert.Equal(
                ranked.Select((_, rank) => rank < roundedUpCount),
                ranked.Select(line => line.RoundedUp));
        }
    }
}
