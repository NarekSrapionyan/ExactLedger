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
        var references = project.Descendants("ProjectReference")
            .Select(item => Path.GetFileNameWithoutExtension(item.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), references);
    }

    [Fact]
    public void Domain_has_no_framework_or_package_dependencies()
    {
        var project = XDocument.Load(Repository.ProjectPath("el.Domain"));
        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void Shared_build_files_do_not_inject_production_dependencies()
    {
        foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            var path = Path.Combine(Repository.Root, file);
            if (!File.Exists(path)) continue;
            var document = XDocument.Load(path);
            Assert.Empty(document.Descendants("ProjectReference"));
            Assert.Empty(document.Descendants("FrameworkReference"));
            Assert.Empty(document.Descendants("PackageReference"));
        }
    }
}
