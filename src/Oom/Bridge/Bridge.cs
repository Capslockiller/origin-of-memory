using System.Text;

namespace Oom.Contracts;

public sealed class Bridge
{
    private const string StartMarker = "<!-- beyin:start -->";
    private const string EndMarker = "<!-- beyin:end -->";

    private readonly string _vault;
    private readonly RootMap _rootMap;
    private readonly IFileOperations _files;

    public Bridge() : this(LaneCVaultPaths.ResolveVault())
    {
    }

    public Bridge(string vaultRoot, RootMap? rootMap = null, IFileOperations? files = null)
    {
        _vault = vaultRoot;
        _files = files ?? new VaultFileOperations();
        _rootMap = rootMap ?? new RootMap(vaultRoot, files: _files);
    }

    public string Refresh()
    {
        var path = Path.Combine(_vault, "CLAUDE.md");
        try
        {
            if (!File.Exists(path))
                return "skip:no-claude-md";
            var text = LaneCVaultPaths.ReadText(path);
            var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
            var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
            if (start < 0 || end < start)
                return "warn:bridge-markers";

            var refreshed = new StringBuilder()
                .Append(text[..(start + StartMarker.Length)])
                .Append('\n')
                .Append(RootMapText().TrimEnd('\n'))
                .Append('\n')
                .Append(text[end..])
                .ToString();
            if (string.Equals(refreshed, text, StringComparison.Ordinal))
                return "ok";
            LaneCVaultPaths.WriteAtomic(path, refreshed, _files);
            return "ok";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "warn:bridge-io";
        }
    }

    private string RootMapText()
    {
        var index = Path.Combine(_vault, "knowledge", "index.md");
        return File.Exists(index) ? LaneCVaultPaths.ReadText(index) : _rootMap.Regenerate();
    }
}
