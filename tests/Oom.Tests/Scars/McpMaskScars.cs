using System.Text;
using System.Text.Json;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

/// <summary>
/// O23 negative-proof oracle for <c>Mcp.CallTool</c>'s output masking (src/Oom/Mcp/Mcp.cs,
/// ~line 110: <c>text = redactor.Mask(text).Text;</c>). Every existing MCP test drives
/// SYNTHETIC, already-clean fixture text, so none of them would notice if that one line
/// were deleted — the whole suite stays green. This file plants a synthetic
/// high-confidence secret (a GitHub-token-shaped string built at runtime, never a real
/// credential, never committed as a literal in this file) inside vault content that both
/// <c>memory_note</c> and <c>memory_root_map</c> serve, and asserts the raw token never
/// reaches the JSON-RPC response — only the masked form does.
/// </summary>
public sealed class McpMaskScars
{
    private static readonly UTF8Encoding Utf8 = new(false);

    // Built at runtime, never a literal secret-shaped string in source: "ghp_" plus 36
    // alphanumeric characters, matching Redactor's own gh[po]_ pattern (Redactor.cs).
    private static readonly string SyntheticToken = "ghp_" + string.Concat(Enumerable.Range(0, 36).Select(i => (char)('a' + i % 26)));

    [Fact(DisplayName = "O23 #1 · memory_note aracı sentetik gizli anahtarı maskeler, ham haliyle döndürmez")]
    public void MemoryNote_MasksSyntheticSecret_NeverReturnsItRaw()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            var concepts = Path.Combine(vault, "knowledge", "concepts");
            Directory.CreateDirectory(concepts);
            File.WriteAllText(Path.Combine(concepts, "gizli-not.md"),
                $"---\ntitle: Gizli Not\naliases: []\ntags: []\nsources: [2026-09-08.md]\ncreated: 2026-09-01\nupdated: 2026-09-08\n---\n" +
                $"Sızdırılan anahtar: {SyntheticToken}\n", Utf8);

            var mcp = new Mcp(new Guards(), new Retrieve(new RetrieveOptions(Top: 5, VaultPath: vault)), vault);
            var text = CallTool(mcp, "memory_note", new { name = "gizli-not.md" });

            Assert.DoesNotContain(SyntheticToken, text, StringComparison.Ordinal);
            Assert.Contains("****(maskelendi)", text, StringComparison.Ordinal);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "O23 #2 · memory_root_map aracı sentetik gizli anahtarı maskeler, ham haliyle döndürmez")]
    public void MemoryRootMap_MasksSyntheticSecret_NeverReturnsItRaw()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, "knowledge"));
            File.WriteAllText(Path.Combine(vault, "knowledge", "index.md"),
                $"- **Bellek** (1 kavram) — sızdırılan anahtar {SyntheticToken} satıra karışmış → [[hubs/bellek]]\n", Utf8);

            var mcp = new Mcp(new Guards(), new Retrieve(new RetrieveOptions(Top: 5, VaultPath: vault)), vault);
            var text = CallTool(mcp, "memory_root_map", new { });

            Assert.DoesNotContain(SyntheticToken, text, StringComparison.Ordinal);
            Assert.Contains("****(maskelendi)", text, StringComparison.Ordinal);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    private static string CallTool(Mcp mcp, string name, object arguments)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name, arguments }
        });
        var response = JsonDocument.Parse(mcp.HandleJsonRpc(request)).RootElement;
        Assert.False(response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null,
            $"{name}: beklenmedik JSON-RPC hatası: {(response.TryGetProperty("error", out var e) ? e.GetRawText() : string.Empty)}");
        return response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
    }
}
