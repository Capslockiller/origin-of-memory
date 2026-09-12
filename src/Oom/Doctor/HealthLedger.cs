using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

internal static class HealthLedger
{
    private static readonly ConcurrentDictionary<string, DoctorObservation> Memory = new(StringComparer.Ordinal);

    internal static void Record(HealthItem item, DateTimeOffset observedAt)
    {
        Memory[$"{item.Component}:{item.Code}:{item.Key}"] = new DoctorObservation(item, observedAt);
        var path = VaultIdentity.ExistingDatabase();
        if (path is null)
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
        }
    }

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

        return observations.Values.OrderBy(observation => observation.Item.Component, StringComparer.Ordinal)
            .ThenBy(observation => observation.Item.Code, StringComparer.Ordinal).ToArray();
    }
}
