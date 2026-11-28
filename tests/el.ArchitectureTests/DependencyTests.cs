using System.Xml.Linq;

namespace el.ArchitectureTests;

public sealed class DependencyTests
{
    [Theory]
    [InlineData("el.Domain", new string[0])]
    [InlineData("el.Application", new[] { "el.Domain" })]
    [InlineData("el.Infrastructure", new[] { "el.Application", "el.Domain" })]
    [InlineData("el.Api", new[] { "el.Application", "el.Infrastructure" })]
    public void Production_projects_follow_the_allowed_reference_graph(string name, string[] expected)
    {
        var project = XDocument.Load(Repository.ProjectPath(name));
        var references = project.Descendants().Where(item => item.Name.LocalName == "ProjectReference")
            .Select(item => Path.GetFileNameWithoutExtension(item.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), references);
    }

    [Theory]
    [InlineData("el.Domain")]
    [InlineData("el.Application")]
    public void Financial_projects_have_no_framework_or_package_dependencies(string name)
    {
        var project = XDocument.Load(Repository.ProjectPath(name));
        Assert.DoesNotContain(project.Descendants(), item => item.Name.LocalName is "FrameworkReference" or "PackageReference" or "Reference");
    }

    [Fact]
    public void Shared_build_files_do_not_inject_production_dependencies()
    {
        foreach (var path in BuildFiles(Repository.Root))
        {
            Assert.True(!InjectedReferences(XDocument.Load(path)).Any(), "Dependency injected by " + path);
        }
    }

    private static IEnumerable<string> BuildFiles(string root) => Directory.EnumerateFiles(root)
        .Concat(Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories))
        .Where(path => Path.GetExtension(path) is ".props" or ".targets")
        .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    private static IEnumerable<XElement> InjectedReferences(XDocument document) => document.Descendants()
        .Where(item => item.Name.LocalName is "ProjectReference" or "FrameworkReference" or "PackageReference" or "Reference");

    [Theory]
    [InlineData("ProjectReference")]
    [InlineData("FrameworkReference")]
    [InlineData("PackageReference")]
    [InlineData("Reference")]
    public void Nested_build_files_and_namespaced_reference_items_are_inspected(string item)
    {
        var root = Path.Combine(Repository.Root, "artifacts", "architecture-probes", Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "src", "el.Domain", "Nested");
        Directory.CreateDirectory(nested);
        var path = Path.Combine(nested, "Directory.Build.targets");
        File.WriteAllText(path, "<Project xmlns='http://schemas.microsoft.com/developer/msbuild/2003'><ItemGroup><" + item + " Include='Unexpected' /></ItemGroup></Project>");
        Assert.Contains(path, BuildFiles(root));
        Assert.Single(InjectedReferences(XDocument.Load(path)));
    }
}
