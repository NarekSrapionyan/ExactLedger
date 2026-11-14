namespace el.ArchitectureTests;

internal static class Repository
{
    internal static string Root { get; } = FindRoot();

    internal static string ProjectPath(string name) => Path.Combine(Root, "src", name, name + ".csproj");

    internal static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(Root, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExactLedger.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate ExactLedger.sln above the test output directory.");
    }
}
