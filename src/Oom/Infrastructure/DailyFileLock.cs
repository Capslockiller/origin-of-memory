using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Oom.Contracts;

internal static class DailyFileLock
{
    internal static FileStream Acquire(string daily)
    {
        var directory = Path.Combine(Path.GetTempPath(), "oom", "locks");
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(daily).ToLowerInvariant())))[..16];
        var path = Path.Combine(directory, key + ".lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(100);
            }
        }
    }
}
