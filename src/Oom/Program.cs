using System.Text;
using System.Text.Json;
using Oom.Contracts;

namespace Oom;

/// <summary>
/// The single entry point (spec 5). It stays thin on purpose: it resolves the
/// vault, applies the recursion guard and hands the work to the component that
/// owns it. No behaviour lives here.
/// </summary>
internal static class Program
{
    private const string RecursionGuard = "OOM_INVOKED_BY";

    /// <summary>Commands a hook may trigger; inside an oom-invoked process they are silent (scar 10.1 #19).</summary>
    private static readonly string[] GuardedCommands = ["context", "retrieve", "flush", "sweep", "compile"];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);

        var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;
        if (command.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        if (GuardedCommands.Contains(command) && Environment.GetEnvironmentVariable(RecursionGuard) is { Length: > 0 })
            return 0;

        var now = DateTimeOffset.Now;
        var vault = VaultPaths.ReadVault() ?? Environment.CurrentDirectory;

        switch (command)
        {
            case "context":
                Console.WriteLine(new Context().Build(vault, now).Text);
                return 0;

            case "retrieve":
            {
                var query = Value(args, "--query");
                var session = Value(args, "--session") ?? "cli";
                var result = args.Contains("--hook")
                    ? new Retrieve().Hook(ReadStandardInput(), session)
                    : new Retrieve().Query(query ?? string.Empty, session);
                Console.WriteLine(result.Output);
                return result.ExitCode;
            }

            case "flush":
            {
                var session = Value(args, "--session") ?? string.Empty;
                var reason = Value(args, "--reason") switch
                {
                    "precompact" => FlushReason.PreCompact,
                    "sweep" => FlushReason.Sweep,
                    "ingest" => FlushReason.Ingest,
                    _ => FlushReason.SessionEnd
                };
                var result = new Flush().FlushSession(session, Value(args, "--transcript") ?? string.Empty, reason);
                Console.WriteLine($"flush: {result.Outcome}");
                return 0;
            }

            case "sweep":
            {
                var result = new Sweep().Run([], new SweepOptions());
                Console.WriteLine($"tarama: {result.Covered}/{result.Total} kapsandı, {result.Skipped} atlandı");
                return 0;
            }

            case "compile":
            {
                var decision = new Compile().MaybeCompile(now, null, hasPending: true);
                Console.WriteLine(decision.ShouldCompile ? "derleme başlıyor" : $"derleme atlandı: {decision.Reason}");
                return 0;
            }

            case "ingest":
            {
                var source = args.Length > 1 ? args[1] : "claude";
                var sessions = new Ingest().Run(source, [], ReadInt(args, "--max"));
                Console.WriteLine($"içe aktarım: {sessions.Count} oturum");
                return 0;
            }

            case "doctor":
            {
                var result = args.Contains("--fix") ? new Doctor().Fix() : new Doctor().Check(now);
                Console.WriteLine(args.Contains("--json")
                    ? JsonSerializer.Serialize(result)
                    : $"kapsama {result.Coverage:P0} · ret {result.RejectionRate:P0} · bekleyen {result.Pending}");
                return 0;
            }

            case "save":
            {
                var sessionJson = Value(args, "--session-json");
                if (sessionJson is not null)
                {
                    var imported = new Save().SaveSessionJson(File.ReadAllText(sessionJson, new UTF8Encoding(false)));
                    Console.WriteLine($"kayıt: {imported.Outcome}");
                    return 0;
                }

                var text = args.Length > 1 ? args[1] : ReadStandardInput();
                var written = new Save().WriteCheckpoint(text, ["karar", "düzeltme", "devir"]);
                Console.WriteLine(written.Written ? "kayıt yazıldı" : $"kayıt yazılmadı: {written.Error}");
                return written.Written ? 0 : 1;
            }

            case "mcp":
            {
                Console.WriteLine(new Mcp().MemoryRootMap());
                return 0;
            }

            case "install":
            {
                var result = args.Contains("--uninstall")
                    ? new Install().Uninstall(vault)
                    : new Install().Run(vault, args.Contains("--from-v0"));
                Console.WriteLine(result.Success ? "kurulum tamam" : $"kurulum başarısız: {result.Error}");
                return result.Success ? 0 : 1;
            }

            case "bench":
                Console.WriteLine("ölçüm araçları bench/ altındadır: 'oom bench' sonuçları bench/results/ içine yazılır.");
                return 0;

            default:
                PrintUsage();
                return 1;
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int? ReadInt(string[] args, string name) =>
        int.TryParse(Value(args, name), out var value) ? value : null;

    private static string ReadStandardInput() => Console.In.ReadToEnd();

    private static void PrintUsage()
    {
        Console.WriteLine("""
            oom — Origin of Memory

            Kullanım: oom <komut> [seçenekler]

              context                          Oturum başlangıcı bağlam bloğunu basar
              retrieve --hook | --query <soru>  İlgili notları getirir
              flush --session <id> [--reason]   Bir oturumu özetler
              sweep [--dry-run]                 Asıl yazma yolu: taramayı koşar
              compile [--dry-run]               Daily'leri kavram notlarına derler
              ingest claude|codex [--max N]     Arşivi geri doldurur
              doctor [--fix] [--json]           Sağlık ve onarım
              save "<metin>" | --session-json   Daily'ye doğrudan kayıt
              mcp [--enable|--disable]          Salt okunur MCP sunucusu
              install [--uninstall] [--from-v0] Kurulum ve göç
              bench [--backend claude|local]    Ölçüm koşumu
            """);
    }
}
