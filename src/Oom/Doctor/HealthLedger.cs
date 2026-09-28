using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

internal static class HealthLedger
{
    /// <summary>S7: a failed health write is kept next to state.db, so a later `oom doctor`
    /// shows it; the file goes away with the next health write that succeeds.</summary>
    internal const string FailureFile = "health-yazilamadi.log";

    private static readonly ConcurrentDictionary<string, DoctorObservation> Memory = new(StringComparer.Ordinal);

    private static volatile bool _writable;

    /// <summary>R24(b): read-only commands (manual context, doctor, dry runs, retrieve,
    /// MCP) never write. A finding reaches state.db only once this process has opened state
    /// for writing (<see cref="State"/> with <see cref="StateAccess.ReadWrite"/> calls this);
    /// before that it is kept in memory, where the same process's `oom doctor` still shows
    /// it.</summary>
    internal static void AllowWrites() => _writable = true;

    internal static void Record(HealthItem item, DateTimeOffset observedAt)
    {
        Memory[$"{item.Component}:{item.Code}:{item.Key}"] = new DoctorObservation(item, observedAt);
        var path = VaultIdentity.ExistingDatabase();
        if (!_writable || path is null)
            return;

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Cache=Shared");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO health(ts, component, level, code, key, detail) VALUES($ts,$component,$level,$code,$key,$detail)";
            command.Parameters.AddWithValue("$ts", observedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$component", item.Component);
            command.Parameters.AddWithValue("$level", item.Level.ToString());
            command.Parameters.AddWithValue("$code", item.Code);
            command.Parameters.AddWithValue("$key", item.Key);
            command.Parameters.AddWithValue("$detail", item.Detail);
            command.ExecuteNonQuery();
        }
        catch (Exception error) when (error is IOException or SqliteException or UnauthorizedAccessException)
        {
            ReportFailure(path, item, observedAt, error);
            return;
        }

        ClearFailure(path, item);
    }

    // Should-fix (review, applied): a later successful write used to delete the WHOLE
    // health-yazilamadi.log, so a transient failure on one component/code was erased by an
    // unrelated successful write before doctor ever surfaced it — violating the fail-loud
    // contract. Only the lines for the SAME component/code as the write that just succeeded
    // are dropped; evidence of other, still-unresolved failures stays.
    private static void ClearFailure(string database, HealthItem item)
    {
        var path = FailurePath(database);
        try
        {
            if (!File.Exists(path))
                return;

            var marker = $"\t{item.Component}/{item.Code}\t";
            var remaining = File.ReadAllLines(path).Where(line => line.Length > 0 && !line.Contains(marker, StringComparison.Ordinal)).ToArray();
            if (remaining.Length == 0)
                File.Delete(path);
            else
                File.WriteAllLines(path, remaining);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"health yazılamadı işareti silinemedi: {error.Message}");
        }
    }

    private static void ReportFailure(string database, HealthItem item, DateTimeOffset observedAt, Exception error)
    {
        var message = error.Message.ReplaceLineEndings(" ");
        Console.Error.WriteLine($"health yazılamadı: {item.Component}/{item.Code} ({item.Key}) — {message}");
        try
        {
            File.AppendAllText(FailurePath(database),
                $"{observedAt.ToString("O", CultureInfo.InvariantCulture)}\t{item.Component}/{item.Code}\t{message}\n");
        }
        catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"health yazılamadı işareti de yazılamadı: {markerError.Message}");
        }
    }

    private static DoctorObservation? ReadFailure(string database)
    {
        var path = FailurePath(database);
        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).Where(line => line.Length > 0).ToArray() : [];
            if (lines.Length == 0)
                return null;
            var last = lines[^1].Split('\t');
            var observed = DateTimeOffset.TryParse(last[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp) ? stamp : File.GetLastWriteTime(path);
            return new DoctorObservation(new HealthItem("health", HealthLevel.Error, "yazilamadi", "state.db",
                $"health yazılamadı: {lines.Length} kayıt kaybedildi, sonuncusu {string.Join(" — ", last.Skip(1))} ({path})"), observed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new DoctorObservation(new HealthItem("health", HealthLevel.Error, "yazilamadi", "state.db",
                $"health yazılamadı işareti okunamadı: {path} — {error.Message}"), DateTimeOffset.Now);
        }
    }

    private static string FailurePath(string database) => Path.Combine(Path.GetDirectoryName(database)!, FailureFile);

    internal static IReadOnlyList<DoctorObservation> Read()
    {
        var observations = Memory.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var path = VaultIdentity.ExistingDatabase();
        if (path is not null && File.Exists(path))
            try
            {
                using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT ts, component, level, code, key, detail FROM health ORDER BY rowid";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var item = new HealthItem(reader.GetString(1), Enum.Parse<HealthLevel>(reader.GetString(2)),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5));
                    var observed = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                    observations[$"{item.Component}:{item.Code}:{item.Key}"] = new DoctorObservation(item, observed);
                }
            }
            catch (SqliteException)
            {
            }

        if (path is not null && ReadFailure(path) is { } failure)
            observations["health:yazilamadi:state.db"] = failure;

        return observations.Values.OrderBy(observation => observation.Item.Component, StringComparer.Ordinal)
            .ThenBy(observation => observation.Item.Code, StringComparer.Ordinal).ToArray();
    }
}
