using el.Domain.Clearing;

namespace el.Domain.Tests.Clearing;

public sealed class Rfc4180Tests
{
    private static string[][] Parse(string text)
    {
        var parsed = Rfc4180.TryParse(text, out var records, out var errorRecord);

        Assert.True(parsed, "Unexpected failure in record " + errorRecord);

        return records.Select(record => record.ToArray()).ToArray();
    }

    [Fact]
    public void Write_ends_every_record_with_crlf_and_quotes_only_when_needed()
    {
        var text = Rfc4180.Write(new[]
        {
            new[] { "a", "b" },
            new[] { "c,d", "e\"f", "g\nh", "i\rj", string.Empty },
        });

        Assert.Equal("a,b\r\n\"c,d\",\"e\"\"f\",\"g\nh\",\"i\rj\",\r\n", text);
    }

    [Fact]
    public void Write_of_no_records_is_empty()
    {
        Assert.Empty(Rfc4180.Write(Array.Empty<string[]>()));
    }

    [Fact]
    public void Write_rejects_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => Rfc4180.Write(null!));
        Assert.Throws<ArgumentNullException>(() => Rfc4180.Write(new[] { new string[] { null! } }));
    }

    [Theory]
    [InlineData("a,b\r\nc,d\r\n")]
    [InlineData("a,b\nc,d\n")]
    [InlineData("a,b\r\nc,d")]
    [InlineData("a,b\nc,d")]
    public void Parse_accepts_crlf_or_lf_and_an_optional_final_line_break(string text)
    {
        Assert.Equal(new[] { new[] { "a", "b" }, new[] { "c", "d" } }, Parse(text));
    }

    [Fact]
    public void Parse_reads_empty_fields_and_a_trailing_comma()
    {
        Assert.Equal(new[] { new[] { "a", string.Empty, "c" } }, Parse("a,,c"));
        Assert.Equal(new[] { new[] { "a", string.Empty } }, Parse("a,"));
        Assert.Equal(new[] { new[] { "a", string.Empty } }, Parse("a,\n"));
        Assert.Equal(new[] { new[] { string.Empty, string.Empty } }, Parse(","));
    }

    [Fact]
    public void Parse_reads_quoted_fields_with_commas_quotes_and_line_breaks()
    {
        var records = Parse("\"a,b\",\"c\"\"d\",\"e\r\nf\"\r\n\"\",x");

        Assert.Equal(new[] { new[] { "a,b", "c\"d", "e\r\nf" }, new[] { string.Empty, "x" } }, records);
    }

    [Fact]
    public void A_blank_line_is_a_record_with_one_empty_field()
    {
        Assert.Equal(new[] { new[] { "a" }, new[] { string.Empty }, new[] { "b" } }, Parse("a\n\nb"));
    }

    [Fact]
    public void Empty_text_has_no_records()
    {
        Assert.Empty(Parse(string.Empty));
    }

    [Theory]
    [InlineData("\"abc", 1)]
    [InlineData("\"", 1)]
    [InlineData("a\"b", 1)]
    [InlineData("\"a\"b", 1)]
    [InlineData("a\rb", 1)]
    [InlineData("a\r", 1)]
    [InlineData("x\ny\"z", 2)]
    [InlineData("x\r\ny,\"open", 2)]
    public void Parse_reports_the_record_of_malformed_text(string text, int expectedRecord)
    {
        Assert.False(Rfc4180.TryParse(text, out var records, out var errorRecord));
        Assert.Empty(records);
        Assert.Equal(expectedRecord, errorRecord);
    }

    [Fact]
    public void Write_then_parse_round_trips_special_characters()
    {
        var records = new[]
        {
            new[] { "x,y", "he said \"hi\"", "line1\r\nline2", string.Empty },
            new[] { "plain", string.Empty, "é€", "z" },
        };

        Assert.Equal(records, Parse(Rfc4180.Write(records)));
    }

    [Fact]
    public void Parse_rejects_null_text()
    {
        Assert.Throws<ArgumentNullException>(() => Rfc4180.TryParse(null!, out _, out _));
    }
}
