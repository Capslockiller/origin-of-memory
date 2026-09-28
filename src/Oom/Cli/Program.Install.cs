using System.Text.Json;
using System.Text.Json.Nodes;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    internal static int RunInstall(string[] args, string vault)
    {
        var uninstalling = args.Contains("--uninstall");
        var executable = Executable();
        var oom = Path.Combine(vault, ".oom");
        if (!uninstalling)
        {
            foreach (var (name, path) in new[] { ("kasa", vault), ("oom.exe", executable) })
                if (HookTemplates.UnquotableCharacter(path) is { } unsafeCharacter)
                {
                    Console.Error.WriteLine($"kurulum: {name} yolu '{unsafeCharacter}' karakterini içeriyor; kanca komutu bu karakterle bash ve cmd altında güvenle yazılamaz. Bu karakteri içermeyen bir yol kullanın: {path}");
                    return 1;
                }

            Directory.CreateDirectory(oom);
            WriteIfAbsent(Path.Combine(oom, "vault.json"), JsonSerializer.Serialize(new { vault, schema = 1 }) + "\n");
            WriteIfAbsent(Path.Combine(oom, "oom.json"), OomSettings.DefaultJson());
        }

        var settingsPath = Path.Combine(vault, ".claude", "settings.json");
        JsonObject root;
        try
        {
            root = (File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath, Utf8).TrimStart('﻿')) : null) as JsonObject ?? [];
        }
        catch (JsonException error)
        {
            Console.Error.WriteLine($"kurulum: {settingsPath}: {error.Message}");
            return 1;
        }

        var hooks = root["hooks"] as JsonObject ?? [];
        foreach (var registration in HookTemplates.Build(executable, vault))
        {
            var kept = new JsonArray();
            if (hooks[registration.Event] is JsonArray existing)
                foreach (var entry in existing)
                    if (entry is not null && !IsOurs(entry, executable))
                        kept.Add(entry.DeepClone());

            if (!uninstalling)
                kept.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = registration.Command,
                        ["timeout"] = registration.TimeoutSeconds
                    })
                });

            if (kept.Count > 0)
                hooks[registration.Event] = kept;
            else
                hooks.Remove(registration.Event);
        }

        if (hooks.Count > 0)
            root["hooks"] = hooks;
        else
            root.Remove("hooks");

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        if (File.Exists(settingsPath))
        {
            var stamp = Clock.Now;
            var backup = $"{settingsPath}.bak-{stamp:yyyyMMdd-HHmmss}";
            while (File.Exists(backup))
                backup = $"{settingsPath}.bak-{(stamp = stamp.AddSeconds(1)):yyyyMMdd-HHmmss}";
            File.Copy(settingsPath, backup);
        }
        var temporary = settingsPath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", Utf8);
        File.Move(temporary, settingsPath, overwrite: true);
        if (!uninstalling)
            foreach (var item in HookHealth(vault).Where(item => item.Level == HealthLevel.Error))
                Console.Error.WriteLine($"kurulum uyarısı: {item.Code}: {item.Detail}");
        Console.WriteLine(uninstalling
            ? $"kaldırma: {settingsPath}"
            : $"kurulum: {oom} · {settingsPath}");
        return 0;
    }

    private static void WriteIfAbsent(string path, string content)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, content, Utf8);
    }

    private static bool IsOurs(JsonNode entry, string executable) =>
        entry["hooks"] is JsonArray commands
        && commands.Any(hook => hook?["command"]?.GetValue<string>() is { } text
            && text.Replace('\\', '/').Contains(executable.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
}
