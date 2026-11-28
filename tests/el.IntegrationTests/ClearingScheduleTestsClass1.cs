using System.Globalization;
using el.Api.Clearing;

namespace el.IntegrationTests;

public sealed class ClearingScheduleTests
{
    [Theory]
    [InlineData("2026-11-22T00:04:59Z", "2026-11-22T00:05:00Z")]
    [InlineData("2026-11-22T00:05:00Z", "2026-11-23T00:05:00Z")]
    [InlineData("2026-11-22T23:59:00Z", "2026-11-23T00:05:00Z")]
    public void NextRun_is_the_next_configured_utc_time(string now, string expected)
    {
        var next = ClearingScheduleService.NextRun(
            DateTimeOffset.Parse(now, CultureInfo.InvariantCulture), new TimeOnly(0, 5));

        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), next);
    }

    [Theory]
    [InlineData("00:05", true)]
    [InlineData("23:59", true)]
    [InlineData("0:5", false)]
    [InlineData("24:00", false)]
    [InlineData("12:00:00", false)]
    [InlineData("", false)]
    public void The_schedule_time_must_use_hh_mm(string value, bool valid)
    {
        Assert.Equal(valid, new ClearingScheduleOptions { TimeUtc = value }.TryGetTime(out _));
    }

    [Fact]
    public void The_schedule_is_enabled_and_at_00_05_utc_by_default()
    {
        var options = new ClearingScheduleOptions();

        Assert.True(options.Enabled);
        Assert.True(options.TryGetTime(out var time));
        Assert.Equal(new TimeOnly(0, 5), time);
    }
}
