using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static Retrieve MakeRetrieve(string vault, OomSettings settings, int top) => new(settings.Retrieve with
    {
        Top = top,
        TotalChars = Math.Max(settings.Retrieve.TotalChars, top * settings.Retrieve.PerNoteChars),
        VaultPath = vault,
        IndexPath = VaultPaths.StateDatabase(),
        // The hand layer and Duzeltmeler live in the configured companion directory, the same one
        // the context reads; Retrieve itself refuses a directory outside the vault.
        CompanionDir = settings.Context.CompanionDir
    });

    private static Runner MakeRunner(string vault, OomSettings settings) =>
        new(new RunnerProfile(vault, settings.Backend.Claude));

    private static State OpenState() => new(null, null, VaultPaths.EnsureStateDatabase(), StateAccess.ReadWrite);

    private static State? OpenStateForReading() => State.OpenReadOnly();

    private static State? OpenStateForUpdate() =>
        VaultIdentity.ExistingDatabase() is { } path ? new State(null, null, path, StateAccess.ReadWrite) : null;

    private static void ReportIndex(VerifyResult index)
    {
        if (index.ExitCode != 0)
            Console.Error.WriteLine($"indeks: eksik {index.Missing.Count}, fazla {index.Extra.Count}"
                + (index.Missing.Count > 0 ? $" · eksik: {string.Join(", ", index.Missing.Take(5))}" : string.Empty)
                + (index.Extra.Count > 0 ? $" · fazla: {string.Join(", ", index.Extra.Take(5))}" : string.Empty));
    }

    private static string ReadIfPresent(string path) => File.Exists(path) ? File.ReadAllText(path, Utf8) : string.Empty;

    private static string Executable() => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "oom.exe");

    private static FlushReason Reason(string reason) => reason switch
    {
        "precompact" => FlushReason.PreCompact,
        "sweep" => FlushReason.Sweep,
        "ingest" => FlushReason.Ingest,
        _ => FlushReason.SessionEnd
    };

    private static string Command(string[] args) => CommandLine.Command(args);

    private static string? Argument(string[] args, int index) => CommandLine.Argument(args, index);

    private static string? Value(string[] args, string name) => CommandLine.Value(args, name);

    private static int? ReadInt(string[] args, string name) =>
        int.TryParse(Value(args, name), out var value) ? value : null;

    private static string ReadStandardInput()
    {
        if (!Console.IsInputRedirected)
            return string.Empty;

        var read = Task.Run(Console.In.ReadToEnd);
        return read.Wait(TimeSpan.FromSeconds(2)) ? read.Result : string.Empty;
    }

    private static void TrySetInputEncoding()
    {
        try
        {
            Console.InputEncoding = Utf8;
        }
        catch (Exception error) when (error is IOException or PlatformNotSupportedException)
        {
        }
    }
}
