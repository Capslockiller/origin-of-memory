using System.Runtime.InteropServices;
using System.Text;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private const string RecursionGuard = "OOM_INVOKED_BY";

    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly string[] GuardedCommands = ["context", "retrieve"];

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
            Console.Error.WriteLine($"hata: {error.GetType().Name}: {error.Message.Replace('\r', ' ').Replace('\n', ' ')}");
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

        if (GuardedCommands.Contains(command) && Environment.GetEnvironmentVariable(RecursionGuard) is { Length: > 0 })
            return 0;

        if (VaultPaths.ReadVault() is not { } vault)
        {
            Console.Error.WriteLine("vault: yok");
            return command == "kaydet" ? 2 : 1;
        }

        if (command == "kaydet" && !Directory.Exists(vault))
        {
            Console.Error.WriteLine("vault: yok");
            return 2;
        }

        var settings = OomSettings.Load(vault);
        if (command != "kaydet") ReportConfigurationWarnings(settings);
        var now = Clock.Now;

        switch (command)
        {
            case "context":
                return Announce(args, vault, settings, now);

            case "retrieve":
                return RunRetrieve(args, vault, settings);

            case "kaydet":
                return RunKaydet(args, vault, settings, now);

            case "durum":
                return RunDurum(args, vault, settings, now);

            case "baglam":
                return RunBaglam(args, vault, settings, now);

            case "doctor":
                return Health(args, vault, settings, now);

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
              kaydet [--tetik mesaj|compact|kapanis|tara] [--session ID --transcript PATH]  yeni turları daily/ içine kaydeder
              durum [--uzun] [--json]                            tek satır kasa ve kayıt durumunu gösterir
              baglam [--metin]                                   oturum başlangıcı bağlamını üretir
              retrieve --query <sorgu> [--json] [--top N] [--batch <dosya>]  indekslenmiş notlarda arar
                --query <sorgu>                                  aranacak sorguyu belirtir
                --json                                            eşleşmeleri JSON olarak gösterir
                --top N                                           en fazla N eşleşme döndürür
                --batch <dosya>                                  dosyadaki her satırı ayrı sorgu olarak işler
              doctor [--fix] [--json] [--quiet] [--all]          kasa, indeks, kanca ve durum sağlığını denetler
                --fix                                             durum ve arama indeksini onarır
                --json                                            sağlık sonucunu JSON olarak gösterir
                --quiet                                           yalnız uyarı ve hataları stderr'e yazar
                --all                                             bilgi düzeyindeki tüm satırları da gösterir
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
