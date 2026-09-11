// yazan: codex · gpt-5
using System.Security.Cryptography;
using System.Text;
using Oom.Contracts;

namespace Oom.Tests;

public sealed class ConfigurationUpgradeTests
{
    [Fact]
    public void Load_LegacyLocalhostUrl_PreservesPreferencesWithoutRewritingFile()
    {
        var vault = CreateVaultWithConfiguration("http://localhost:22445/custom/v1");
        var path = Path.Combine(vault, ".oom", "oom.json");
        try
        {
            var hashBefore = SHA256.HashData(File.ReadAllBytes(path));

            var settings = OomSettings.Load(vault);

            Assert.Equal(17, settings.Sweep.EveryHours);
            Assert.Equal(["D:\\memory", "E:\\archive"], settings.Sweep.Roots);
            Assert.Equal(5, settings.Compile.EveningHour);
            Assert.Equal(45678, settings.Context.CapChars);
            Assert.Equal("custom-companion", settings.Context.CompanionDir);
            Assert.Equal(11, settings.Retrieve.Top);
            Assert.False(settings.McpEnabled);
            Assert.False(settings.Toast);
            Assert.Equal("custom-fast", settings.Backend.Claude.Fast);
            Assert.Equal("custom-smart", settings.Backend.Claude.Smart);
            Assert.Equal("http://127.0.0.1:22445/custom/v1", settings.Backend.Local.Url);
            Assert.Equal("sample", Assert.Single(settings.Extensions).Name);
            Assert.Null(settings.LoadError);
            Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally
        {
            Directory.Delete(vault, true);
        }
    }

    [Theory]
    [InlineData("http://localhost.evil:11434/v1")]
    [InlineData("http://example.test:11434/v1")]
    [InlineData("http://user@localhost:11434/v1")]
    [InlineData("https://localhost:11434/v1")]
    public void Load_UnsafeHostnameForms_RemainRejected(string url)
    {
        var vault = CreateVaultWithConfiguration(url);
        try
        {
            var settings = OomSettings.Load(vault);

            Assert.NotNull(settings.LoadError);
            Assert.Equal(OomSettings.Defaults(vault).Backend.Local.Url, settings.Backend.Local.Url);
        }
        finally
        {
            Directory.Delete(vault, true);
        }
    }

    private static string CreateVaultWithConfiguration(string url)
    {
        var vault = Path.Combine(Path.GetTempPath(), $"oom-configuration-upgrade-{Guid.NewGuid():N}");
        var directory = Path.Combine(vault, ".oom");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "oom.json"), $$"""
            {
              "backend": {
                "claude": { "fast": "custom-fast", "smart": "custom-smart" },
                "local": { "url": "{{url}}" }
              },
              "sweep": { "everyHours": 17, "roots": ["D:\\memory", "E:\\archive"] },
              "compile": { "eveningHour": 5 },
              "context": { "companionDir": "custom-companion", "capChars": 45678 },
              "retrieve": { "top": 11 },
              "mcp": { "enabled": false },
              "notify": { "toast": false },
              "extensions": [{ "name": "sample", "contextLine": "sample-context" }]
            }
            """, new UTF8Encoding(false));
        return vault;
    }
}
