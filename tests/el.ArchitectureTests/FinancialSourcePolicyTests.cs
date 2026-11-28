namespace el.ArchitectureTests;

public sealed class FinancialSourcePolicyTests
{
    [Fact]
    public void Domain_and_Application_sources_obey_the_financial_policy()
    {
        var sources = new[] { "el.Domain", "el.Application" }
            .SelectMany(Repository.Sources)
            .Select(path => (Path: path, Text: File.ReadAllText(path)));

        var violations = FinancialSourcePolicy.Check(sources);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("public double Calculate() => 0.1 + 0.2;", "EL001")]
    [InlineData("public float Calculate() => 1f;", "EL001")]
    [InlineData("public System.Half Calculate() => (System.Half)1;", "EL001")]
    [InlineData("public decimal Calculate(decimal n, decimal d) => n / d;", "EL002")]
    [InlineData("public decimal Calculate(decimal n, decimal d) { n /= d; return n; }", "EL002")]
    [InlineData("public decimal Calculate(decimal n, decimal d) => decimal.Divide(n, d);", "EL002")]
    [InlineData("public System.DateTime Read() => System.DateTime.UtcNow;", "EL003")]
    [InlineData("public System.DateTimeOffset Read() => System.DateTimeOffset.Now;", "EL003")]
    [InlineData("public System.DateTimeOffset Read() => System.TimeProvider.System.GetUtcNow();", "EL003")]
    [InlineData("public string Read() => System.Globalization.CultureInfo.CurrentCulture.Name;", "EL004")]
    [InlineData("public string Read() => System.Threading.Thread.CurrentThread.CurrentCulture.Name;", "EL004")]
    [InlineData("public object Read() => System.Globalization.NumberFormatInfo.CurrentInfo;", "EL004")]
    [InlineData("public System.Net.Http.HttpClient Create() => new();", "EL005")]
    [InlineData("public string Serialize() => System.Text.Json.JsonSerializer.Serialize(1);", "EL005")]
    [InlineData("public System.Data.Common.DbConnection Read() => null!;", "EL005")]
    [InlineData("public System.Net.Sockets.Socket Read() => null!;", "EL005")]
    [InlineData("public object Read() => new System.Xml.Serialization.XmlSerializer(typeof(string));", "EL005")]
    [InlineData("public T Divide<T>(T n,T d) where T : System.Numerics.INumber<T> => n / d; public decimal Read() => Divide(1m,3m);", "EL002")]
    public void Invalid_financial_code_is_rejected(string member, string rule)
    {
        var violations = FinancialSourcePolicy.Check([("NegativeFixture.cs", "public class Fixture { " + member + " }")]);
        Assert.Contains(violations, violation => violation.Contains(rule, StringComparison.Ordinal));
    }

    [Fact]
    public void Aliases_and_static_imports_do_not_bypass_symbol_checks()
    {
        const string source = """
            using Clock = System.DateTime;
            using static System.Globalization.CultureInfo;
            public class Fixture
            {
                public string Read() => Clock.UtcNow.ToString(CurrentCulture);
            }
            """;
        var violations = FinancialSourcePolicy.Check([("AliasFixture.cs", source)]);
        Assert.Contains(violations, item => item.Contains("EL003", StringComparison.Ordinal));
        Assert.Contains(violations, item => item.Contains("EL004", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DEBUG")]
    [InlineData("RELEASE")]
    [InlineData("DEBUG && RELEASE")]
    [InlineData("CUSTOM_FINANCE")]
    public void Conditional_financial_code_is_checked_in_every_configuration(string condition)
    {
        var source = "public class Fixture {\n#if " + condition + "\npublic double Read() => 1.0;\n#else\npublic int Read() => 1;\n#endif\n}";
        Assert.Contains(FinancialSourcePolicy.Check([("Conditional.cs", source)]), item => item.Contains("EL001", StringComparison.Ordinal));
    }

    [Fact]
    public void Integer_generic_math_division_is_allowed()
    {
        const string source = "public class Fixture { public T Divide<T>(T n,T d) where T : System.Numerics.IBinaryInteger<T> => n / d; }";
        Assert.Empty(FinancialSourcePolicy.Check([("IntegerGeneric.cs", source)]));
    }

    [Fact]
    public void Too_many_conditional_symbols_fail_closed()
    {
        var conditions = string.Join("\n", Enumerable.Range(0, 9).Select(i => "#if FLAG" + i + "\n#endif"));
        Assert.Throws<InvalidOperationException>(() => FinancialSourcePolicy.Check([("Complex.cs", conditions)]));
    }

    [Fact]
    public void Exact_integers_explicit_time_and_invariant_formatting_are_allowed()
    {
        const string source = """
            public class Fixture
            {
                public string Calculate(long amount, long rate, System.DateTimeOffset suppliedTime)
                {
                    var numerator = (System.Numerics.BigInteger)amount * rate;
                    var quotient = System.Numerics.BigInteger.DivRem(numerator, 100000000, out var remainder);
                    return quotient.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            """;
        Assert.Empty(FinancialSourcePolicy.Check([("ValidFixture.cs", source)]));
    }

    [Fact]
    public void Invalid_fixture_syntax_cannot_be_mistaken_for_a_passing_policy()
    {
        Assert.Throws<InvalidOperationException>(() => FinancialSourcePolicy.Check([("Broken.cs", "not valid C#")]));
    }
}
