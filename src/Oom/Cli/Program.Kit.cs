using System.Text.Json;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    internal static int RunKit(string[] args) => RunKit(args, HomeRoot());

    internal static int RunKit(string[] args, string homeRoot)
    {
        var sub = Argument(args, 0) ?? string.Empty;
        var kit = new Kit(Value(args, "--kit") ?? DefaultKitRoot(), homeRoot);

        return sub switch
        {
            "status" => RunKitStatus(kit, args),
            "install" => RunKitInstall(kit, args),
            _ => PrintKitUsage()
        };
    }

    private static int RunKitStatus(Kit kit, string[] args)
    {
        IReadOnlyList<KitRow> rows;
        try
        {
            rows = kit.Status();
        }
        catch (Exception error) when (TryDescribeKitError(error, out var detail))
        {
            Console.Error.WriteLine(detail);
            return 1;
        }

        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema_version = 1,
                kit_yok = kit.KitRootMissing,
                items = rows.Select(row => new { name = row.Name, target = row.Target, state = row.State.Text(), detail = row.Detail })
            }));
        }
        else
        {
            if (kit.KitRootMissing)
                Console.WriteLine("kit yok");
            foreach (var row in rows)
                Console.WriteLine($"{row.Name} {row.Target} {row.State.Text()}: {row.Detail}");
        }

        return rows.All(row => row.State == KitState.Guncel) ? 0 : 1;
    }

    private static int RunKitInstall(Kit kit, string[] args)
    {
        if (kit.KitRootMissing)
        {
            Console.WriteLine("kit yok");
            return 1;
        }

        var only = Value(args, "--only");
        try
        {
            if (only is not null && !kit.HasComponent(only))
            {
                Console.WriteLine($"bilinmeyen bileşen: {only}");
                return 1;
            }

            var result = kit.Install(args.Contains("--dry-run"), args.Contains("--force"), only);
            foreach (var outcome in result.Outcomes)
                Console.WriteLine($"{outcome.Name} {outcome.Target} {outcome.Action}"
                    + (string.IsNullOrEmpty(outcome.Detail) ? string.Empty : $": {outcome.Detail}"));

            return result.ExitCode;
        }
        catch (Exception error) when (TryDescribeKitError(error, out var detail))
        {
            Console.Error.WriteLine(detail);
            return 1;
        }
    }

    private static bool TryDescribeKitError(Exception error, out string detail)
    {
        if (error is JsonException or IOException or UnauthorizedAccessException)
        {
            detail = $"manifest okunamadı: {error.Message}";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static int PrintKitUsage()
    {
        PrintUsage();
        return 1;
    }

    // Should-fix (review, applied): mirrors Program.Doctor.UserProfileRoot()'s existing
    // OOM_USERPROFILE seam. Without this, `kit install --force`/`kit status` and the `kit`
    // rows a plain `doctor` computes internally (KitObservations -> HomeRoot()) always
    // resolved the real ~/.claude and ~/.agents, even under a process-boundary test
    // harness — so a flag/command matrix that exercises every documented flag once could
    // back up and overwrite the developer's own real kit installation. Unset, behaviour is
    // unchanged.
    private static string HomeRoot() =>
        Environment.GetEnvironmentVariable("OOM_USERPROFILE") is { Length: > 0 } overridden
            ? overridden
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string DefaultKitRoot() => Path.Combine(Path.GetDirectoryName(Executable()) ?? AppContext.BaseDirectory, "kit");
}
