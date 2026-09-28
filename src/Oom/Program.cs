using System.Runtime.InteropServices;
using System.Text;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private const string RecursionGuard = "OOM_INVOKED_BY";

    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly string[] GuardedCommands = ["context", "retrieve", "nudge", "flush", "sweep", "compile"];

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    private static int Main(string[] args) => Main(args, Dispatch);

    private static int Main(string[] args, Func<string[], int> dispatch)
    {
        if (OperatingSystem.IsWindows())
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);

        // The tool speaks Turkish; numbers and percentages ("%6,1") must not change with the
        // machine's regional settings (measured: an en-US CI runner printed a different form).
        var turkish = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = turkish;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = turkish;
        System.Globalization.CultureInfo.CurrentCulture = turkish;
        System.Globalization.CultureInfo.CurrentUICulture = turkish;

        try
        {
            return dispatch(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"hata: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        Console.OutputEncoding = Utf8;
        TrySetInputEncoding();

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 0;
        }

        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(BuildInfo.Version);
            return 0;
        }

        VaultPaths.UseVault(Value(args, "--vault"));

        var command = Command(args);
        if (command.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        if (command == "kit")
        {
            if (VaultPaths.ReadVault() is { } kitVault)
                ReportConfigurationWarnings(OomSettings.Load(kitVault));
            return RunKit(args);
        }

        if (GuardedCommands.Contains(command) && Environment.GetEnvironmentVariable(RecursionGuard) is { Length: > 0 })
            return 0;

        if (VaultPaths.ReadVault() is not { } vault)
        {
            Console.Error.WriteLine("vault: yok");
            return 1;
        }

        var settings = OomSettings.Load(vault);
        ReportConfigurationWarnings(settings);
        var now = Clock.Now;

        switch (command)
        {
            case "context":
                return Announce(args, vault, settings, now);

            case "retrieve":
                return RunRetrieve(args, vault, settings);

            case "flush":
                return RunFlush(args, vault, settings);

            case "sweep":
                return RunSweep(args, vault, settings, now);

            case "compile":
                return RunCompile(args, vault, settings, now);

            case "doctor":
                return Health(args, vault, settings, now);

            case "save":
                return RunSave(args, vault);

            case "mcp":
            {
                if (!settings.McpEnabled)
                {
                    Console.Error.WriteLine("mcp: kapalı");
                    return 1;
                }

                new Mcp(new Guards(), MakeRetrieve(vault, settings, 5), vault).Run(Console.In, Console.Out);
                return 0;
            }

            case "install":
                return RunInstall(args, vault);

            case "nudge":
                return RunNudge(args, vault, settings, now);

            default:
                PrintUsage();
                return 1;
        }
    }

    private static IClock Clock { get; } = SystemClock.Instance;

    private static void PrintUsage()
    {
        Console.WriteLine("""
            oom [--vault <yol>] <komut>

              --vault <yol>                                      kasa kökü olarak <yol> kullanılır

              context [--json]                                   oturum başlangıcı hafıza bağlamını gösterir
                --json                                            bölümleri ve metni JSON olarak gösterir
              nudge [--session <kimlik>]                         istemi kaydeder ve oturum zamanlamasını gösterir
                --session <kimlik>                               kanca oturumu yerine bu oturumu kullanır
              flush [--session <kimlik>] [--reason <neden>]      oturum dökümünü daily/ içine özetler
                --session <kimlik>                               özetlenecek oturumu belirtir
                --transcript <dosya>                             kanca dökümü yerine bu dosyayı okur
                --reason <neden>                                 nedeni belirtir: precompact, sweep, ingest, sessionend
                --detached                                        ayrı süreç başlatmadan aynı süreç içinde işler
              sweep [--dry-run]                                  değişen dökümleri yapılandırılmış köklerde tarar
                --dry-run                                         yazmadan ve indekslemeden sonucu gösterir
              compile [--dry-run]                                bekleyen daily kayıtlarını bilgi notlarına derler
                --dry-run                                         daily kayıtlarını derlemeden planı gösterir
              retrieve --query <sorgu> [--json] [--top N] [--batch <dosya>]  indekslenmiş notlarda arar
                --query <sorgu>                                  aranacak sorguyu belirtir
                --json                                            eşleşmeleri JSON olarak gösterir
                --top N                                           en fazla N eşleşme döndürür
                --batch <dosya>                                  dosyadaki her satırı ayrı sorgu olarak işler
              doctor [--fix] [--json] [--quiet] [--all]          kasa, indeks, kanca ve durum sağlığını denetler
                --fix                                             durum, kök harita ve arama indeksini onarır
                --json                                            sağlık sonucunu JSON olarak gösterir
                --quiet                                           yalnız uyarı ve hataları stderr'e yazar
                --all                                             bilgi düzeyindeki tüm satırları da gösterir
              save "<serbest metin>" | --session-json <dosya>    bugünün daily kaydına denetim noktası ekler
                --session-json <dosya>                            oturum JSON dosyasından denetim noktası alır
              mcp                                                 salt okunur MCP arabirimini stdio üzerinden çalıştırır
              install [--uninstall]                              Claude kanca kayıtlarını kurar veya kaldırır
                --uninstall                                       bu yürütülebilir dosyanın Claude kancalarını kaldırır
              kit status [--json] [--kit <dizin>]                bileşen × hedef başına kuru/güncel/farklı/bozuk durumunu göster
                --json                                            kit status çıktısını JSON olarak üret
                --kit <dizin>                                     kit kökü olarak <exe-dizini>/kit yerine <dizin> kullan
              kit install [--dry-run] [--force] [--only <ad>] [--kit <dizin>]  kit bileşenlerini ev profiline kur
                --dry-run                                         hiçbir şey yazmadan neyin değişeceğini bildir
                --force                                           farklı olan hedefi yedekleyip üzerine yaz
                --only <ad>                                       yalnızca bu adlı bileşenle sınırla
                --kit <dizin>                                     kit kökü olarak <exe-dizini>/kit yerine <dizin> kullan
            """);
    }

    private static void ReportConfigurationWarnings(OomSettings settings)
    {
        if (settings.LoadError is { } error)
            Console.Error.WriteLine($"ayar dosyası okunamadı, varsayılanlar kullanılıyor: {error}");

        foreach (var key in settings.UnknownKeys.Distinct(StringComparer.Ordinal))
            Console.Error.WriteLine($"bilinmeyen ayar: {key}");

        foreach (var item in settings.InvalidValues)
            Console.Error.WriteLine($"geçersiz ayar değeri: {item.Key} = {item.Value} ({item.Fallback} kullanılıyor)");
    }
}
