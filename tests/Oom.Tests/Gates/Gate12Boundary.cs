namespace Oom.Tests.Gates;

public sealed class Gate12Boundary
{
    private static readonly string[] PackageNames = ["oom-kota", "oom-rapor", "oom-ingest-extra", "oom-pano"];

    [Fact(DisplayName = "Kapı 12-4 · src/Oom hiçbir Aşama 2 paket adını tanımaz")]
    public void Gate12CoreKnowsNoPackageName()
    {
        var core = Path.Combine(GateFixture.RepositoryRoot(), "src", "Oom");
        var separator = Path.DirectorySeparatorChar;
        var sources = Directory.EnumerateFiles(core, "*", SearchOption.AllDirectories)
            .Where(path => !path.Split(separator, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .ToArray();

        Assert.NotEmpty(sources);
        var offenders = new List<string>();
        foreach (var path in sources)
        {
            var content = File.ReadAllText(path);
            offenders.AddRange(PackageNames
                .Where(name => content.Contains(name, StringComparison.OrdinalIgnoreCase))
                .Select(name => $"{Path.GetFileName(path)}: {name}"));
        }

        Assert.Empty(offenders);
    }
}
