using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Oom.Contracts;

/// <summary>
/// R31: a per-vault+session cross-process lock, held from the moment a flush reads its
/// cursor through its final commit, so a hook+hook or hook+sweep race can never
/// summarise the same range twice. Keyed by state directory + session id, under a
/// "locks" subfolder of the state dir — a sibling of, but distinct from,
/// <see cref="DailyFileLock"/>, which guards the DAILY FILE against two different
/// sessions' writes colliding, not one session's cursor against itself.
/// </summary>
internal static class SessionLock
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// Blocks up to 10 s waiting for the lock. Returns the held <see cref="FileStream"/>
    /// (dispose it to release), or null once the 10 s window elapses without acquiring it
    /// — the caller reports 'busy' without ever calling the model.
    /// </summary>
    internal static FileStream? TryAcquire(string stateDirectory, string sessionId)
    {
        var directory = Path.Combine(stateDirectory, "locks");
        try
        {
            Directory.CreateDirectory(directory);
        }
        // R31/R36 (#10): an unwritable state dir (locked-down %LOCALAPPDATA%, a read-only
        // profile, or the "locks" subfolder removed/ACL-changed between State's own
        // constructor creating the state dir and this call) must fail the same clean way
        // a lock-file contention timeout already does — the caller (Flush.FlushSession)
        // turns a null return into FlushOutcome.Locked, a reported outcome, not a crash.
        // This was previously completely unguarded: everything below the catch below is
        // scoped to the lock FILE, never the directory that has to exist first.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var key = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(sessionId)))[..16];
        var path = Path.Combine(directory, "session-" + key + ".lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < Timeout)
            {
                Thread.Sleep(PollInterval);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
