using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace el.ArchitectureTests;

// A test-only source policy for Domain/Application; API and Infrastructure are outside its scope.
internal static class FinancialSourcePolicy
{
    private static readonly MetadataReference[] References =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime reference assemblies are unavailable."))
        .Split(Path.PathSeparator)
        .Where(path => !Path.GetFileName(path).StartsWith("el.", StringComparison.Ordinal))
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    internal static IReadOnlyList<string> Check(IEnumerable<(string Path, string Text)> sources)
    {
        var materialized = sources.ToArray();
        var symbols = materialized.SelectMany(source => CSharpSyntaxTree.ParseText(source.Text).GetRoot()
                .DescendantTrivia(descendIntoTrivia: true).Select(trivia => trivia.GetStructure())
                .Select(directive => directive switch
                {
                    IfDirectiveTriviaSyntax condition => condition.Condition,
                    ElifDirectiveTriviaSyntax condition => condition.Condition,
                    _ => null,
                }).OfType<ExpressionSyntax>()
                .SelectMany(condition => condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                .Select(identifier => identifier.Identifier.ValueText))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Exhaustive configurations are bounded; too many symbols must fail, never silently skip code.
        if (symbols.Length > 8) throw new InvalidOperationException("Financial policy supports up to eight conditional symbols; simplify the financial build configurations.");
        var violations = new HashSet<string>(StringComparer.Ordinal);
        for (var mask = 0; mask < (1 << symbols.Length); mask++)
        {
            var selected = symbols.Where((_, index) => (mask & (1 << index)) != 0);
            violations.UnionWith(CheckConfiguration(materialized, selected, symbols));
        }
        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> CheckConfiguration((string Path, string Text)[] sources,
        IEnumerable<string> selectedSymbols, string[] conditionalSymbols)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp14,
            preprocessorSymbols: new[] { "TRACE", "NET", "NET10_0", "NET10_0_OR_GREATER", "NETCOREAPP" }
                .Except(conditionalSymbols, StringComparer.Ordinal).Concat(selectedSymbols));
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, options, source.Path)).ToList();
        trees.Add(CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """, options, "PolicyGlobalUsings.g.cs"));
        var compilation = CSharpCompilation.Create("ExactLedger.PolicyFixture", trees, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, checkOverflow: true));
        var errors = compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
        {
            throw new InvalidOperationException("Source policy cannot inspect invalid code:\n" + string.Join("\n", errors.Select(item => item.ToString())));
        }

        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in trees.Where(tree => tree.FilePath != "PolicyGlobalUsings.g.cs"))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is ExpressionSyntax or TypeSyntax)
                {
                    var type = model.GetTypeInfo(node).Type;
                    if (type?.SpecialType is SpecialType.System_Double or SpecialType.System_Single || type?.ToDisplayString() == "System.Half")
                        Report("EL001", "Binary floating-point values are forbidden in financial code.");

                    if (node.IsKind(SyntaxKind.DivideExpression) || node.IsKind(SyntaxKind.DivideAssignmentExpression))
                    {
                        if (type?.SpecialType == SpecialType.System_Decimal
                            || (type is ITypeParameterSymbol parameter && !parameter.ConstraintTypes.Any(IsIntegerConstraint)))
                            Report("EL002", "Use exact integer quotient/remainder instead of decimal division.");
                    }
                }

                var symbol = model.GetSymbolInfo(node).Symbol;
                if (symbol is null) continue;
                var owner = symbol.ContainingType?.ToDisplayString();
                if (owner == "decimal" && symbol.Name is "Divide" or "op_Division")
                    Report("EL002", "Use exact integer quotient/remainder instead of decimal division.");
                if ((owner is "System.DateTime" or "System.DateTimeOffset" && symbol.Name is "Now" or "UtcNow" or "Today")
                    || (owner == "System.TimeProvider" && symbol.Name == "System"))
                    Report("EL003", "Supply time explicitly instead of reading the system clock.");

                if ((owner == "System.Globalization.CultureInfo" && symbol.Name is "CurrentCulture" or "CurrentUICulture" or "DefaultThreadCurrentCulture" or "DefaultThreadCurrentUICulture")
                    || (owner is "System.Globalization.NumberFormatInfo" or "System.Globalization.DateTimeFormatInfo" && symbol.Name == "CurrentInfo")
                    || (owner == "System.Threading.Thread" && symbol.Name is "CurrentCulture" or "CurrentUICulture"))
                    Report("EL004", "Use explicit invariant culture in financial code.");

                var ns = (symbol as INamedTypeSymbol)?.ContainingNamespace.ToDisplayString()
                    ?? symbol.ContainingType?.ContainingNamespace.ToDisplayString();
                if (ns is not null && new[] { "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "System.Net", "System.Data", "System.Xml.Serialization", "System.Text.Json", "Newtonsoft.Json", "Npgsql", "Dapper" }
                    .Any(prefix => ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal)))
                    Report("EL005", "Keep HTTP, serialization and persistence dependencies outside Domain/Application.");

                void Report(string rule, string message)
                {
                    var position = tree.GetLineSpan(node.Span).StartLinePosition;
                    violations.Add($"{tree.FilePath}:{position.Line + 1}: {rule} {message}");
                }
            }
        }

        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsIntegerConstraint(ITypeSymbol constraint) => constraint is INamedTypeSymbol named
        && named.AllInterfaces.Prepend(named).Any(type => type.OriginalDefinition.ToDisplayString() == "System.Numerics.IBinaryInteger<TSelf>");
}
