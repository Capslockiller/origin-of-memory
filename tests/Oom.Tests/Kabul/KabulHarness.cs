using System.Diagnostics;
using System.Text;

namespace Oom.Tests.Kabul;

/// <summary>
/// Process-boundary acceptance harness: runs a real <c>oom</c> executable as a child
/// process against a fixture vault and captures stdout, stderr, exit code and elapsed
/// time. Later acceptance lanes use this to prove a defect against the OLD exe and a
/// fix against the NEW exe, from the same test code.
///
/// The exe under test is chosen by the <c>OOM_KABUL_EXE</c> environment variable when
/// set (an absolute path to any built oom.exe — old or new), otherwise it falls back to
/// the oom.exe that MSBuild copies next to this test assembly's own build output
/// (Oom.csproj is an Exe project referenced by Oom.Tests.csproj, so its output lands in
/// this assembly's bin directory as a side effect of the ProjectReference).
///
/// The isolated temp root is created under THIS TEST ASSEMBLY'S OWN OUTPUT DIRECTORY
/// (AppContext.BaseDirectory), never under %TEMP%: the existing Scars suite resolves
/// some paths relative to the OS temp directory and is location-dependent as a result
/// (audit A2-12) — Kabul avoids repeating that mistake. OOM_LOCALAPPDATA is pointed at a
/// subdirectory of that same isolated root, so the child process's state.db, root map
/// and search index never touch the real %LOCALAPPDATA%\oom directory.
///
/// O21: OOM_USERPROFILE is pointed at another subdirectory of that same isolated root by
/// DEFAULT, on every <see cref="Run"/>/<see cref="RunAsync"/> call, the same way
/// OOM_LOCALAPPDATA already is — so a doctor-running (or kit-running) child never reads
/// this developer's REAL ~/.claude/settings.json or kit installation, and a green fixture
/// never depends on what happens to be installed on the machine running the suite. A test
/// that needs a DIFFERENT synthetic profile (e.g. to plant a specific settings.json) still
/// overrides it per call, the same way <c>fakeNow</c> overrides OOM_FAKE_NOW: pass
/// <c>userProfile</c>.
/// </summary>
public sealed class KabulHarness : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string _root;
    private bool _disposed;

    /// <summary>Absolute path to the oom executable this harness will invoke.</summary>
    public string ExePath { get; }

    /// <summary>The isolated temp root created for this harness instance (under the test output dir).</summary>
    public string Root => _root;

    /// <summary>Subdirectory of <see cref="Root"/> passed to the child as OOM_LOCALAPPDATA.</summary>
    public string LocalAppData { get; }

    /// <summary>
    /// Subdirectory of <see cref="Root"/> passed to the child as OOM_USERPROFILE by default
    /// (O21) — never this machine's real user profile. Empty (no .claude directory), so a
    /// doctor/kit child sees "no settings, no kit" rather than whatever this developer's
    /// machine happens to have installed. A test needing different profile contents creates
    /// its own directory (e.g. via <see cref="NewScratchDirectory"/>) and passes it as the
    /// <c>userProfile</c> argument to <see cref="Run"/>/<see cref="RunAsync"/> instead of
    /// using this one.
    /// </summary>
    public string UserProfile { get; }

    public KabulHarness(string? exePath = null)
    {
        ExePath = exePath ?? ResolveExePath();
        if (!File.Exists(ExePath))
            throw new FileNotFoundException($"oom executable not found: {ExePath}", ExePath);

        _root = CreateIsolatedRoot();
        LocalAppData = Path.Combine(_root, "localappdata");
        Directory.CreateDirectory(LocalAppData);
        UserProfile = Path.Combine(_root, "userprofile");
        Directory.CreateDirectory(UserProfile);
        // The default sweep root (%USERPROFILE%/.claude/projects) must exist in the isolated
        // profile, or doctor on a machine without one (a CI runner) reports every root missing.
        Directory.CreateDirectory(Path.Combine(UserProfile, ".claude", "projects"));
    }

    /// <summary>
    /// Resolves the exe under test: OOM_KABUL_EXE if set (must point at an existing
    /// file), otherwise oom.exe beside this test assembly's own build output.
    /// </summary>
    public static string ResolveExePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("OOM_KABUL_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!File.Exists(overridePath))
                throw new FileNotFoundException(
                    $"OOM_KABUL_EXE is set but points to a missing file: {overridePath}", overridePath);
            return overridePath;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "oom.exe");
        if (File.Exists(beside))
            return beside;

        throw new FileNotFoundException(
            "no oom executable found. Set OOM_KABUL_EXE to an oom.exe path, or build Oom.sln " +
            $"so it lands beside this test assembly's own output ({beside}).", beside);
    }

    private static string CreateIsolatedRoot()
    {
        var runs = Path.Combine(AppContext.BaseDirectory, "kabul-runs");
        Directory.CreateDirectory(runs);
        var root = Path.Combine(runs, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Creates a fresh subdirectory of <see cref="Root"/> a caller can use as a scratch
    /// area (e.g. to build a fixture vault into) without colliding with LocalAppData.
    /// </summary>
    public string NewScratchDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Runs the exe as <c>oom --vault &lt;vaultPath&gt; &lt;args...&gt;</c> and captures
    /// stdout, stderr, exit code and elapsed wall time. OOM_LOCALAPPDATA is always set to
    /// this harness's isolated LocalAppData directory; OOM_INVOKED_BY is always cleared
    /// so the child does not short-circuit as a guarded recursive invocation. OOM_USERPROFILE
    /// defaults to this harness's isolated <see cref="UserProfile"/> (O21) — pass
    /// <paramref name="userProfile"/> to point a doctor/kit child at a different synthetic
    /// profile instead (never this machine's real one).
    /// </summary>
    public KabulResult Run(
        string vaultPath,
        IEnumerable<string> args,
        string? standardInput = null,
        DateTimeOffset? fakeNow = null,
        TimeSpan? timeout = null,
        string? userProfile = null)
        => RunAsync(vaultPath, args, standardInput, fakeNow, timeout, userProfile).GetAwaiter().GetResult();

    /// <inheritdoc cref="Run"/>
    public async Task<KabulResult> RunAsync(
        string vaultPath,
        IEnumerable<string> args,
        string? standardInput = null,
        DateTimeOffset? fakeNow = null,
        TimeSpan? timeout = null,
        string? userProfile = null)
    {
        ArgumentNullException.ThrowIfNull(vaultPath);
        ArgumentNullException.ThrowIfNull(args);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);

        var startInfo = new ProcessStartInfo
        {
            FileName = ExePath,
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        startInfo.ArgumentList.Add("--vault");
        startInfo.ArgumentList.Add(vaultPath);
        foreach (var argument in args)
            startInfo.ArgumentList.Add(argument);

        // The exe treats OOM_INVOKED_BY as "I was spawned by another oom process; skip
        // the guarded command" (Program.GuardedCommands). Kabul processes are direct,
        // top-level invocations, so this must never leak in from the test host process.
        startInfo.Environment.Remove("OOM_INVOKED_BY");
        startInfo.Environment["OOM_LOCALAPPDATA"] = LocalAppData;
        // O21: never left unset — an unset OOM_USERPROFILE falls through to
        // Environment.SpecialFolder.UserProfile, i.e. this developer's REAL ~/.claude and
        // kit installation (Program.Doctor.UserProfileRoot / Program.Kit.HomeRoot both
        // read it). Defaults to this harness's own isolated, empty UserProfile.
        var profile = string.IsNullOrEmpty(userProfile) ? UserProfile : userProfile;
        startInfo.Environment["OOM_USERPROFILE"] = profile;
        // Default sweep roots expand %USERPROFILE% itself; without this the child scanned the
        // developer's real ~/.claude/projects and a CI runner's empty profile gave other results.
        startInfo.Environment["USERPROFILE"] = profile;
        if (fakeNow is { } now)
            startInfo.Environment["OOM_FAKE_NOW"] = now.ToString("O");

        using var process = new Process { StartInfo = startInfo };
        var started = Stopwatch.GetTimestamp();
        process.Start();

        if (!string.IsNullOrEmpty(standardInput))
            await process.StandardInput.WriteAsync(standardInput);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(effectiveTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException(
                $"oom did not exit within {effectiveTimeout}: {ExePath} " +
                string.Join(' ', startInfo.ArgumentList));
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var elapsed = Stopwatch.GetElapsedTime(started);

        return new KabulResult(process.ExitCode, stdout, stderr, elapsed);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Result of one <see cref="KabulHarness.Run"/> invocation.</summary>
public sealed record KabulResult(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed);
