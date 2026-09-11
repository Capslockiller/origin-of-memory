using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class DefterScars
{
    [Fact(DisplayName = "Y-119 · Sonraki başarısız çağrı önceki çağrının tokenlarını miras almaz")]
    public void Y119_LaterExhaustedCallDoesNotInheritEarlierUsage()
    {
        WithDatabase((database, state) =>
        {
            var http = new QueuedHttp(
                "{\"message\":{\"content\":\"ilk\"},\"prompt_eval_count\":12,\"eval_count\":4}",
                "geçerli-json-değil");
            var chains = new Dictionary<ComponentKind, IReadOnlyList<string>> { [ComponentKind.Flush] = ["local"] };
            var runner = new Runner(null, http: http, state: state, configured: true, chains: chains);

            Assert.Null(runner.Run("bir", ModelTier.Fast, ComponentKind.Flush, "summary").Error);
            Assert.NotNull(runner.Run("iki", ModelTier.Fast, ComponentKind.Flush, "summary").Error);

            using var connection = Open(database);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT uncached_in_tok, out_tok, cache_r, cache_w, usage_source FROM calls ORDER BY rowid";
            using var rows = command.ExecuteReader();
            Assert.True(rows.Read());
            Assert.Equal(12L, rows.GetInt64(0));
            Assert.Equal(4L, rows.GetInt64(1));
            Assert.Equal("measured", rows.GetString(4));
            Assert.True(rows.Read());
            Assert.True(rows.IsDBNull(0));
            Assert.True(rows.IsDBNull(1));
            Assert.True(rows.IsDBNull(2));
            Assert.True(rows.IsDBNull(3));
            Assert.Equal("unknown", rows.GetString(4));
            Assert.False(rows.Read());
        });
    }

    [Fact(DisplayName = "Y-120 · Fallback kullanımı yalnız gerçekten çalışan backend'e aittir")]
    public void Y120_FallbackUsageBelongsToTheBackendThatReportedIt()
    {
        WithDatabase((database, state) =>
        {
            var root = Path.GetDirectoryName(database)!;
            var chains = new Dictionary<ComponentKind, IReadOnlyList<string>> { [ComponentKind.Flush] = ["claude", "local"] };
            var profile = new RunnerProfile(root, Path.Combine(root, "claude-config"),
                new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5", "claude-config"),
                // yazan: codex · gpt-5
                new LocalSettings("http://127.0.0.1:11434/v1", "qwen3:8b", "qwen3:14b", "nomic-embed-text"), chains);
            var process = new FixedProcess(new ProcessResult(1, string.Empty, "ulaşılamadı", true));
            var http = new QueuedHttp("{\"message\":{\"content\":\"yerel\"},\"prompt_eval_count\":21,\"eval_count\":5}");
            var runner = new Runner(profile, process, http, state: state);

            var result = runner.Run("özetle", ModelTier.Fast, ComponentKind.Flush, "summary");

            Assert.Null(result.Error);
            Assert.Equal("local", result.Backend);
            Assert.Equal(21L, result.Usage!.UncachedInputTokens);
            using var connection = Open(database);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT backend, uncached_in_tok, out_tok, usage_source, operation_id, attempt_id, attempt_no FROM calls ORDER BY rowid";
            using var rows = command.ExecuteReader();
            Assert.True(rows.Read());
            Assert.Equal("claude", rows.GetString(0));
            Assert.True(rows.IsDBNull(1));
            Assert.True(rows.IsDBNull(2));
            Assert.Equal("unknown", rows.GetString(3));
            var operationId = rows.GetString(4);
            var firstAttemptId = rows.GetString(5);
            Assert.Equal(1L, rows.GetInt64(6));
            Assert.True(rows.Read());
            Assert.Equal("local", rows.GetString(0));
            Assert.Equal(21L, rows.GetInt64(1));
            Assert.Equal(5L, rows.GetInt64(2));
            Assert.Equal("measured", rows.GetString(3));
            Assert.Equal(operationId, rows.GetString(4));
            Assert.NotEqual(firstAttemptId, rows.GetString(5));
            Assert.Equal(2L, rows.GetInt64(6));
            Assert.False(rows.Read());
        });
    }

    [Fact(DisplayName = "Y-121 · Aynı token sayılarına sahip ayrı çağrılar ayrı ledger satırlarıdır")]
    public void Y121_IdenticalUsageDoesNotCollapseDistinctCalls()
    {
        WithDatabase((database, state) =>
        {
            var usage = new TokenUsage(8, 3, 2, 1);
            state.RecordCall("local", ComponentKind.Flush, ModelTier.Fast, "qwen3:8b", 20, 4, 10, "ok", UsageSourceKind.Measured, "summary", "op-1", "attempt-1", 1, usage);
            state.RecordCall("local", ComponentKind.Flush, ModelTier.Fast, "qwen3:8b", 20, 4, 10, "ok", UsageSourceKind.Measured, "summary", "op-2", "attempt-2", 1, usage);

            using var connection = Open(database);
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM v_calls WHERE uncached_in_tok = 8 AND out_tok = 3"));
            Assert.Equal(2L, Scalar(connection, "SELECT attempt_count FROM v_call_usage WHERE backend = 'local' AND purpose = 'summary'"));
            Assert.Equal(2L, Scalar(connection, "SELECT operation_count FROM v_call_usage WHERE backend = 'local' AND purpose = 'summary'"));
        });
    }

    [Fact(DisplayName = "Y-122 · Gözlenmeyen kullanım gerçek sıfırdan ayırt edilir")]
    public void Y122_UnknownUsageIsDistinctFromMeasuredZero()
    {
        WithDatabase((database, state) =>
        {
            state.RecordCall("local", ComponentKind.Flush, ModelTier.Fast, "qwen3:8b", 0, 0, 1, "fallback", UsageSourceKind.Unknown, "unknown", "op-u", "attempt-u", 1, null);
            state.RecordCall("local", ComponentKind.Flush, ModelTier.Fast, "qwen3:8b", 0, 0, 1, "ok", UsageSourceKind.Measured, "zero", "op-z", "attempt-z", 1, new TokenUsage(0, 0, 0, 0));

            using var connection = Open(database);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT uncached_in_tok, out_tok, cache_r, cache_w, usage_source, usage_rank FROM calls ORDER BY rowid";
            using var rows = command.ExecuteReader();
            Assert.True(rows.Read());
            Assert.True(rows.IsDBNull(0));
            Assert.True(rows.IsDBNull(1));
            Assert.True(rows.IsDBNull(2));
            Assert.True(rows.IsDBNull(3));
            Assert.Equal("unknown", rows.GetString(4));
            Assert.Equal(0L, rows.GetInt64(5));
            Assert.True(rows.Read());
            Assert.Equal(0L, rows.GetInt64(0));
            Assert.Equal(0L, rows.GetInt64(1));
            Assert.Equal(0L, rows.GetInt64(2));
            Assert.Equal(0L, rows.GetInt64(3));
            Assert.Equal("measured", rows.GetString(4));
            Assert.Equal(2L, rows.GetInt64(5));
        });
    }

    [Fact(DisplayName = "Y-123 · Sürüm 3 ledger satırları değişmeden kalır ve legacy olarak işaretlenir")]
    public void Y123_MigrationPreservesLegacyRowsWithoutGuessingUncachedInput()
    {
        var root = ScarFixture.TempDirectory();
        var database = Path.Combine(root, "state.db");
        try
        {
            using (var connection = Open(database))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    // Fixture düzeltmesi: damga 3 diyordu, tablo ise sürüm 1 biçimindeydi. Her basamak
                    // her açılışta koştuğu sürece fark görünmüyordu; damga artık inanıldığı için fixture
                    // gerçekten sürüm 3 biçiminde olmalı. Damga (3) ve bütün beklentiler aynen korunuyor.
                    "CREATE TABLE calls(ts TEXT, backend TEXT, component TEXT, tier TEXT, model TEXT, in_chars INTEGER, out_chars INTEGER, in_tok INTEGER, out_tok INTEGER, cache_r INTEGER, cache_w INTEGER, ms INTEGER, outcome TEXT, usage_source TEXT, purpose TEXT, operation_id TEXT, attempt_id TEXT, attempt_no INTEGER, uncached_in_tok INTEGER);" +
                    "CREATE UNIQUE INDEX ix_calls_attempt_id ON calls(attempt_id) WHERE attempt_id IS NOT NULL;" +
                    "INSERT INTO calls(ts, backend, component, tier, model, in_chars, out_chars, in_tok, out_tok, cache_r, cache_w, ms, outcome, usage_source, purpose) VALUES('t','claude','Flush','Fast','m',10,5,15,3,4,1,20,'ok','actual','summary');" +
                    "PRAGMA user_version=3;";
                command.ExecuteNonQuery();
            }

            using (var state = new State(null, null, database))
                Assert.Equal(1L, state.Scalar("SELECT COUNT(*) FROM calls"));

            using var migrated = Open(database);
            using var read = migrated.CreateCommand();
            read.CommandText = "SELECT in_tok, out_tok, cache_r, cache_w, uncached_in_tok, operation_id, attempt_id, usage_rank, usage_semantics FROM calls";
            using var row = read.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal(15L, row.GetInt64(0));
            Assert.Equal(3L, row.GetInt64(1));
            Assert.Equal(4L, row.GetInt64(2));
            Assert.Equal(1L, row.GetInt64(3));
            Assert.True(row.IsDBNull(4));
            Assert.True(row.IsDBNull(5));
            Assert.True(row.IsDBNull(6));
            Assert.Equal(0L, row.GetInt64(7));
            Assert.Equal("legacy-total-input-v0", row.GetString(8));
            Assert.Equal(5L, Scalar(migrated, "PRAGMA user_version"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    private static void WithDatabase(Action<string, State> test)
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var database = Path.Combine(root, "state.db");
            using var state = new State(null, null, database);
            test(database, state);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed class QueuedHttp(params string[] responses) : IHttp
    {
        private readonly Queue<string> _responses = new(responses);
        public string Send(string method, string url, string body) => _responses.Dequeue();
    }

    private sealed class FixedProcess(ProcessResult result) : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) => result;
    }
}
