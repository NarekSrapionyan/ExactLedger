using el.Domain.Ledger;

namespace el.Domain.Tests.Ledger;

public sealed class JournalReferenceTests
{
    [Theory]
    [InlineData("SETTLEMENT:T-1001:CAPTURE")]
    [InlineData("A")]
    [InlineData("BATCH_2026.11.11")]
    public void Accepts_uppercase_ascii_references(string value)
    {
        var reference = JournalReference.Parse(value);

        Assert.Equal(value, reference.Value);
        Assert.Equal(value, reference.ToString());
        Assert.Equal(reference, JournalReference.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("settlement:t-1")]
    [InlineData("SETTLEMENT T-1")]
    [InlineData("SETTLEMENT/T-1")]
    [InlineData("Ä")]
    public void Rejects_other_values(string? value)
    {
        Assert.Throws<ArgumentException>(() => JournalReference.Parse(value!));
        Assert.False(JournalReference.TryParse(value, out var reference));
        Assert.Null(reference);
    }

    [Fact]
    public void Limits_the_length()
    {
        Assert.True(JournalReference.TryParse(new string('R', JournalReference.MaxLength), out _));
        Assert.False(JournalReference.TryParse(new string('R', JournalReference.MaxLength + 1), out _));
    }
}
