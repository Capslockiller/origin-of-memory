using Oom.Contracts;

namespace Oom.Tests.Kabul;

public sealed class CompileRollbackKabul
{
    [Fact]
    public void FailedRollback_IsReportedAndKeepsRecoveryBackup()
    {
        using var harness = new KabulHarness();
        var vault = harness.NewScratchDirectory("vault");
        var stateRoot = harness.NewScratchDirectory("state");
        var target = Path.Combine(vault, "knowledge", "concepts", "mevcut.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "eski icerik");

        using var files = new LockAfterReplaceOperations();
        var compile = new Compile(vault, fileOperations: files, stateRoot: stateRoot);
        var result = compile.Publish("2026-09-27.md",
            new Dictionary<string, string> { ["knowledge/concepts/mevcut.md"] = "yeni icerik" },
            failDuringRebuild: true);

        Assert.True(!result.RolledBack,
            $"expected RolledBack=false when restoration fails; actual RolledBack={result.RolledBack}");
        Assert.False(result.Atomic);
        Assert.True(result.SourcePending);

        var backups = Directory.Exists(Path.Combine(stateRoot, "backup"))
            ? Directory.EnumerateFiles(Path.Combine(stateRoot, "backup"), "mevcut.md", SearchOption.AllDirectories).ToArray()
            : [];
        Assert.True(backups.Length == 1,
            $"expected one retained recovery backup; actual count={backups.Length}");
        Assert.Equal("eski icerik", File.ReadAllText(backups[0]));
        Assert.Equal("yeni icerik", File.ReadAllText(target));
    }

    private sealed class LockAfterReplaceOperations : IFileOperations, IDisposable
    {
        private FileStream? _lock;

        public void Replace(string source, string destination)
        {
            File.Replace(source, destination, null);
            _lock = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public void Dispose() => _lock?.Dispose();
    }
}
