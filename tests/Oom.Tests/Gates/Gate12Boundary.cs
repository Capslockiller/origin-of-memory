using System.Text.Json;
using Oom.Contracts;

namespace Oom.Tests.Gates;

/// <summary>
/// Acceptance gate 12 (spec 11-12), the boundary gate: the four stable interfaces of spec 2.2
/// are the only surface a phase 2 package may bind to, so each one is measured here and the
/// core is scanned for any knowledge of a package. The field lists below are the contract —
/// spec 2.2 allows fields to be added, never removed or renamed, so these tests assert
/// presence, never an exact set.
/// </summary>
public sealed class Gate12Boundary
{
    private static readonly string[] DoctorFields = ["schema_version", "coverage", "rejection_rate", "pending", "exit_code", "items"];
    private static readonly string[] DoctorItemFields = ["Component", "level", "Code", "Key", "Detail", "stale"];
    private static readonly string[] ContextFields = ["schema_version", "sections", "chars", "text"];
    private static readonly string[] RetrieveFields = ["schema_version", "query", "hits"];
    private static readonly string[] RetrieveHitFields = ["name", "score", "source", "updated"];

    /// <summary>Phase 2 package names (spec 2.2, 6.10, 6.13); the core must not know one of them.</summary>
    private static readonly string[] PackageNames = ["oom-kota", "oom-rapor", "oom-ingest-extra", "oom-pano"];

    [Fact(DisplayName = "Kapı 12-1 · doctor --json şema sürümünü ve söz verilen alanları taşır")]
    public void Gate12DoctorJsonCarriesItsSchema()
    {
        using var vault = new TempVault();
        GateFixture.WriteVault(vault.Path);

        // Y-162: taramayı fikstüre yönlendir; yoksa yayımlanan doctor sahibin gerçek
        // %LOCALAPPDATA%\oom altındaki her veritabanını açar.
        var scanRoot = Path.Combine(vault.Path, "profil");
        Directory.CreateDirectory(Path.Combine(scanRoot, "oom"));
        var run = GateFixture.RunScoped(scanRoot, "doctor", "--json", "--vault", vault.Path);
        Assert.Equal(0, run.ExitCode);

        var root = JsonDocument.Parse(run.StandardOutput).RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        foreach (var field in DoctorFields)
            Assert.True(root.TryGetProperty(field, out _), $"doctor --json '{field}' alanını kaybetti.");

        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        foreach (var field in DoctorItemFields)
            Assert.True(items[0].TryGetProperty(field, out _), $"doctor --json item '{field}' alanını kaybetti.");
    }

    [Fact(DisplayName = "Kapı 12-1 · context --json şema sürümünü ve söz verilen alanları taşır")]
    public void Gate12ContextJsonCarriesItsSchema()
    {
        using var vault = new TempVault();
        GateFixture.WriteVault(vault.Path);
        File.WriteAllText(Path.Combine(vault.Path, "daily", "2026-09-09.md"), "# Günlük Log: 2026-09-09\n", GateFixture.Utf8);

        var run = GateFixture.Run("context", "--json", "--vault", vault.Path);
        Assert.Equal(0, run.ExitCode);

        var root = JsonDocument.Parse(run.StandardOutput).RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        foreach (var field in ContextFields)
            Assert.True(root.TryGetProperty(field, out _), $"context --json '{field}' alanını kaybetti.");

        Assert.NotEmpty(root.GetProperty("sections").EnumerateArray());
        Assert.Equal(root.GetProperty("text").GetString()!.Length, root.GetProperty("chars").GetInt32());
    }

    [Fact(DisplayName = "Kapı 12-1 · retrieve --query --json şema sürümünü ve isabet alanlarını taşır")]
    public void Gate12RetrieveJsonCarriesItsSchema()
    {
        using var vault = new TempVault();
        GateFixture.WriteVault(vault.Path);

        var run = GateFixture.Run("retrieve", "--query", "tokenizasyon ölçüsü", "--json", "--vault", vault.Path);
        Assert.Equal(0, run.ExitCode);

        var root = JsonDocument.Parse(run.StandardOutput).RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        foreach (var field in RetrieveFields)
            Assert.True(root.TryGetProperty(field, out _), $"retrieve --json '{field}' alanını kaybetti.");

        Assert.Equal("tokenizasyon ölçüsü", root.GetProperty("query").GetString());
        var hits = root.GetProperty("hits").EnumerateArray().ToArray();
        Assert.NotEmpty(hits);
        foreach (var field in RetrieveHitFields)
            Assert.True(hits[0].TryGetProperty(field, out _), $"retrieve --json hit '{field}' alanını kaybetti.");
    }

    [Fact(DisplayName = "Kapı 12-3 · extensions.contextLine komutu çalışır ve tek satırı bağlama eklenir")]
    public void Gate12ExtensionContextLineIsExecuted()
    {
        using var vault = new TempVault();
        GateFixture.WriteVault(vault.Path);
        WriteExtensions(vault.Path, "{\"name\":\"kota\",\"contextLine\":\"cmd /c echo kota yuzde 42\"}");

        var run = GateFixture.Run("context", "--json", "--vault", vault.Path);
        Assert.Equal(0, run.ExitCode);

        var text = JsonDocument.Parse(run.StandardOutput).RootElement.GetProperty("text").GetString()!;
        Assert.Contains("[kota] kota yuzde 42", text, StringComparison.Ordinal);

        // Spec 7: the closing sentence stays the last line of the block.
        Assert.EndsWith("Hafıza protokolü zorunludur.\n", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Kapı 12-3 · Olmayan ya da hata veren uzantı komutu hiçbir şey eklemez ve context'i düşürmez")]
    public void Gate12FailingExtensionAddsNothing()
    {
        using var vault = new TempVault();
        GateFixture.WriteVault(vault.Path);
        WriteExtensions(vault.Path,
            "{\"name\":\"yok\",\"contextLine\":\"olmayan-komut-e7a1b2 --surum\"}",
            "{\"name\":\"kirik\",\"contextLine\":\"cmd /c exit 3\"}",
            "{\"name\":\"sessiz\",\"contextLine\":\"cmd /c rem\"}");

        var run = GateFixture.Run("context", "--json", "--vault", vault.Path);
        Assert.Equal(0, run.ExitCode);

        var text = JsonDocument.Parse(run.StandardOutput).RootElement.GetProperty("text").GetString()!;
        foreach (var name in new[] { "[yok]", "[kirik]", "[sessiz]" })
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);

        Assert.EndsWith("Hafıza protokolü zorunludur.\n", text, StringComparison.Ordinal);
    }

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

    private static void WriteExtensions(string vault, params string[] entries) =>
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"),
            $"{{\"extensions\":[{string.Join(',', entries)}]}}", GateFixture.Utf8);
}
