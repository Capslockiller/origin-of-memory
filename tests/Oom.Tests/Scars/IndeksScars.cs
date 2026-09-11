// yazan: codex · gpt-5
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class IndeksScars
{
    [Fact(DisplayName = "İndeks · Aynı gün gövde değişince FTS yeniden kurulur")]
    public void SameDayBodyEditRebuildsAndMakesNewTermSearchable()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            WriteNote(vault, "not.md", "ilkbenzersizterim");
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            retrieve.Build();
            var first = ReadManifest(index);
            Assert.Contains("not.md", Candidates(index, "ilkbenzersizterim"));

            WriteNote(vault, "not.md", "yenibenzersizterim"); // `updated` deliberately remains 2026-09-10.
            retrieve.Build();
            var second = ReadManifest(index);

            Assert.True(second.Generation > first.Generation);
            Assert.NotEqual(first.Digest, second.Digest);
            Assert.Contains("not.md", Candidates(index, "yenibenzersizterim"));
            Assert.DoesNotContain("not.md", Candidates(index, "ilkbenzersizterim"));
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "İndeks · Değişmeyen derlem yeniden kurmayı atlar")]
    public void UnchangedCorpusSkipsRebuild()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            WriteNote(vault, "not.md", "sabit-terim");
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            retrieve.Build();
            var first = ReadManifest(index);
            retrieve.Build();
            var second = ReadManifest(index);

            Assert.Equal(first, second);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "İndeks · İçerik değişince manifest değişir, geri dönünce digest korunur")]
    public void AddRemoveAndRenameChangeDigest()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            var firstPath = WriteNote(vault, "bir.md", "ortak-govde");
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            retrieve.Build();
            var original = ReadManifest(index);
            Assert.Contains("bir.md", Candidates(index, "ortak-govde"));

            var addedPath = WriteNote(vault, "iki.md", "eklenen-govde");
            retrieve.Build();
            var added = ReadManifest(index);

            File.Delete(addedPath);
            retrieve.Build();
            var removed = ReadManifest(index);
            Assert.DoesNotContain("iki.md", Candidates(index, "eklenen-govde"));

            var renamedPath = Path.Combine(Path.GetDirectoryName(firstPath)!, "yeniden-adlandirildi.md");
            File.Move(firstPath, renamedPath);
            retrieve.Build();
            var renamed = ReadManifest(index);

            Assert.Equal([1L, 2L, 3L, 4L], new[] { original.Generation, added.Generation, removed.Generation, renamed.Generation });
            Assert.NotEqual(original.Digest, added.Digest);
            Assert.Equal(original.Digest, removed.Digest);
            Assert.NotEqual(removed.Digest, renamed.Digest);
            Assert.Equal(3, new[] { original.Digest, added.Digest, removed.Digest, renamed.Digest }.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain("bir.md", Candidates(index, "ortak-govde"));
            Assert.Contains("yeniden-adlandirildi.md", Candidates(index, "ortak-govde"));
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "İndeks · Farklı derlemler aynı manifestle çakışmaz")]
    public void DifferentCorporaHaveDifferentDigests()
    {
        var left = ScarFixture.TempDirectory();
        var right = ScarFixture.TempDirectory();
        try
        {
            var leftIndex = Path.Combine(left, "state.db");
            var rightIndex = Path.Combine(right, "state.db");
            WriteNote(left, "not.md", "sol-derlem-terimi");
            WriteNote(right, "not.md", "sag-derlem-terimi");

            new Retrieve(new RetrieveOptions(VaultPath: left, IndexPath: leftIndex)).Build();
            new Retrieve(new RetrieveOptions(VaultPath: right, IndexPath: rightIndex)).Build();

            Assert.NotEqual(ReadManifest(leftIndex).Digest, ReadManifest(rightIndex).Digest);
        }
        finally
        {
            ScarFixture.Remove(left);
            ScarFixture.Remove(right);
        }
    }

    [Fact(DisplayName = "İndeks · Dosya sırası digest'i değiştirmez")]
    public void EquivalentCorporaCreatedInDifferentOrdersHaveSameDigest()
    {
        var left = ScarFixture.TempDirectory();
        var right = ScarFixture.TempDirectory();
        try
        {
            WriteNote(left, "bir.md", "birinci-govde");
            WriteNote(left, "iki.md", "ikinci-govde");
            WriteNote(right, "iki.md", "ikinci-govde");
            WriteNote(right, "bir.md", "birinci-govde");
            var leftIndex = Path.Combine(left, "state.db");
            var rightIndex = Path.Combine(right, "state.db");

            new Retrieve(new RetrieveOptions(VaultPath: left, IndexPath: leftIndex)).Build();
            new Retrieve(new RetrieveOptions(VaultPath: right, IndexPath: rightIndex)).Build();

            Assert.Equal(ReadManifest(leftIndex).Digest, ReadManifest(rightIndex).Digest);
        }
        finally
        {
            ScarFixture.Remove(left);
            ScarFixture.Remove(right);
        }
    }

    [Fact(DisplayName = "İndeks · Başlık, takma ad ve etiket değişikliği yeniden kurulur")]
    public void IndexedMetadataChangesRebuildAndBecomeSearchable()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            WriteNote(vault, "not.md", "govde", "ilk-baslik", "ilk-takma", "ilk-etiket");
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            retrieve.Build();
            var first = ReadManifest(index);

            WriteNote(vault, "not.md", "govde", "yeni-baslik", "ilk-takma", "ilk-etiket");
            retrieve.Build();
            var titleChanged = ReadManifest(index);
            WriteNote(vault, "not.md", "govde", "yeni-baslik", "yeni-takma", "ilk-etiket");
            retrieve.Build();
            var aliasesChanged = ReadManifest(index);
            WriteNote(vault, "not.md", "govde", "yeni-baslik", "yeni-takma", "yeni-etiket");
            retrieve.Build();
            var tagsChanged = ReadManifest(index);

            Assert.True(titleChanged.Generation > first.Generation);
            Assert.True(aliasesChanged.Generation > titleChanged.Generation);
            Assert.True(tagsChanged.Generation > aliasesChanged.Generation);
            Assert.NotEqual(first.Digest, titleChanged.Digest);
            Assert.NotEqual(titleChanged.Digest, aliasesChanged.Digest);
            Assert.NotEqual(aliasesChanged.Digest, tagsChanged.Digest);
            Assert.Contains("not.md", Candidates(index, "yeni-baslik"));
            Assert.Contains("not.md", Candidates(index, "yeni-takma"));
            Assert.Contains("not.md", Candidates(index, "yeni-etiket"));
            Assert.DoesNotContain("not.md", Candidates(index, "ilk-baslik"));
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    // yazan: claude · opus-5
    [Fact(DisplayName = "Y-140 · FTS aday kümesi, sıralayıcının puanladığı hiçbir notu düşürmez")]
    public void Y140_FtsCandidatesCoverEveryNoteTheInProcessRankerScores()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            // One decisive Turkish word per note over a shared filler phrase, so a probe that reaches
            // the right note through the ranker but not through the index shows up as a set
            // difference and not as a ranking nuance.
            WriteNote(vault, "a.md", "zümrütlü sıradan gövde", "A başlık");
            WriteNote(vault, "b.md", "kapısı sıradan gövde", "B başlık");
            WriteNote(vault, "c.md", "çalışıyor sıradan gövde", "C başlık");
            WriteNote(vault, "d.md", "güvenliktenmiş sıradan gövde", "D başlık");
            WriteNote(vault, "e.md", "ISTANBUL sıradan gövde", "E başlık");

            var indexed = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            indexed.Build();
            // The ranker's own answer has to come from an instance with no index, or the comparison
            // is the FTS candidate set against itself and can never fail.
            var scanner = new Retrieve(new RetrieveOptions(VaultPath: vault));

            using var connection = new SqliteConnection($"Data Source={index}");
            connection.Open();

            foreach (var probe in new[] { "zümrütlü", "kapısı", "çalışıyor", "güvenlikten", "istanbul", "ıstanbul" })
            {
                var ranked = scanner.Query(probe, Guid.NewGuid().ToString(), 50).Hits.Select(hit => hit.Name).ToArray();
                var candidates = indexed.Candidates(connection, probe, 50);
                Assert.Equal("corpus-scan:no-index", scanner.CandidateSource);
                Assert.NotEmpty(ranked);
                // Measured before the index was built out of folded tokens: `kapısı`, `çalışıyor` and
                // `güvenlikten` each reached their note through the ranker and returned an EMPTY FTS
                // candidate set. unicode61 strips the diacritic and keeps the dotless i; TurkishFold
                // keeps the diacritic and folds ı onto i (Y-044), and the five-character suffix
                // prefixes it emits were never in the index at all.
                Assert.Empty(ranked.Except(candidates, StringComparer.Ordinal));
            }
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    /// <summary>
    /// `Retrieve.Build()` used to call <c>Directory.CreateDirectory</c> on the index's parent
    /// directory before opening it, which minted a state root as a side effect of merely asking to
    /// index — one per test run, one per <c>compile</c> against a workspace that had never run
    /// <c>oom install</c>. An existing directory is now the condition for writing an index, not
    /// something Build brings into being; an absent one is a legitimately empty answer, not a
    /// broken one.
    /// </summary>
    [Fact(DisplayName = "Y-171 · İndeks kurulumu durum kökü yaratmaz")]
    public void Y171_BuildDoesNotCreateAStateRoot()
    {
        var vault = ScarFixture.TempDirectory();
        var missingDirectory = Path.Combine(vault, "yok-boyle-bir-dizin");
        var index = Path.Combine(missingDirectory, "state.db");
        try
        {
            WriteNote(vault, "not.md", "govde-y171");
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            var before = retrieve.Build();

            Assert.False(Directory.Exists(missingDirectory), "Build var olmayan dizini yaratmamalıydı.");
            Assert.False(File.Exists(index), "Build dizin yokken indeks dosyası yaratmamalıydı.");
            // İndeksin yokluğu bozuk bir indeks değil, ölçülmüş boş bir cevaptır.
            Assert.Equal(0, before.ExitCode);
            Assert.Empty(before.Missing);
            Assert.Empty(before.Extra);

            Directory.CreateDirectory(missingDirectory);
            var after = retrieve.Build();

            Assert.True(File.Exists(index), "Dizin var olduktan sonra Build indeks dosyasını yaratmalıydı.");
            Assert.Equal(0, after.ExitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }

    /// <summary>
    /// A rebuild used to open its own connection to <c>state.db</c> and <c>DROP TABLE notes</c> /
    /// <c>DROP TABLE notes_fts</c> before recreating them from scratch — destroying whatever the
    /// schema owner (<c>StateStore</c>) had already put in that same file, including
    /// <c>ix_notes_updated</c>, in a file that also carries the owner's own ledger
    /// (<c>flush_log</c>, <c>PRAGMA user_version</c>). A rebuild now empties and refills the index
    /// tables inside a transaction and drops nothing it does not own.
    /// </summary>
    [Fact(DisplayName = "Y-172 · Yeniden kurulum şema sahibinin tablolarını düşürmez")]
    public void Y172_RebuildDoesNotDropTheSchemaOwnersTables()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            using (var state = new State(null, null, index))
                state.RecordFlush(ScarFixture.Now, "y172", "sessionend", "ok", 4, 40, "claude");
            SqliteConnection.ClearAllPools();

            WriteNote(vault, "not.md", "eski-terim-y172");
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            retrieve.Build();
            var first = ReadManifest(index);

            WriteNote(vault, "not.md", "yeni-terim-y172"); // gövde değişir, gerçek bir yeniden kurulum tetiklenir.
            retrieve.Build();
            var second = ReadManifest(index);
            Assert.True(second.Generation > first.Generation, "İkinci Build gerçek bir yeniden kurulum yapmalıydı.");

            using (var connection = new SqliteConnection($"Data Source={index}"))
            {
                connection.Open();
                using (var owned = connection.CreateCommand())
                {
                    owned.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_notes_updated'";
                    // Şema sahibinin indeksi yeniden kurulumdan sağ çıkmalı — eski DROP TABLE altında bu 0 çıkardı.
                    Assert.Equal(1L, (long)owned.ExecuteScalar()!);
                }

                using (var flush = connection.CreateCommand())
                {
                    flush.CommandText = "SELECT COUNT(*) FROM flush_log";
                    Assert.Equal(1L, (long)flush.ExecuteScalar()!); // Yabancı satıra dokunulmamalı.
                }

                using (var version = connection.CreateCommand())
                {
                    version.CommandText = "PRAGMA user_version";
                    Assert.Equal(5L, Convert.ToInt64(version.ExecuteScalar())); // Dosya hâlâ sahibi tarafından damgalı.
                }
            }

            Assert.Contains("not.md", Candidates(index, "yeni-terim-y172"));
            Assert.DoesNotContain("not.md", Candidates(index, "eski-terim-y172"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }

    /// <summary>
    /// `Build()` used to trust an unchanged manifest digest completely: when the digest matched, it
    /// skipped the rebuild, verified nothing, and returned success even though the index rows
    /// underneath it had been tampered with by hand. `Build` now grades its own work on every call,
    /// `VerifyIndex()` answers the identical read-only question, and <c>Doctor</c> reports the same
    /// verdict through both its raw <see cref="VerifyResult"/> overload and its
    /// <see cref="HealthItem"/> summary — one answer to "is the index sound", never a second opinion
    /// that can call it clean while the first one calls it broken.
    /// </summary>
    [Fact(DisplayName = "Y-176 · Build kendi işini doğrular; bozuk indeks kırmızı çıkar ve doctor aynı cevabı verir")]
    public void Y176_BuildVerifiesItsOwnWorkAndDoctorAgrees()
    {
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            WriteNote(vault, "bir.md", "birinci-govde-y176");
            WriteNote(vault, "iki.md", "ikinci-govde-y176");
            var sound = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index)).Build();
            Assert.Equal(0, sound.ExitCode); // Sağlam derlem üzerinde ilk kurulum sağlam çıkmalı.
            Assert.Empty(sound.Missing);

            using (var connection = new SqliteConnection($"Data Source={index}"))
            {
                connection.Open();
                using var tamper = connection.CreateCommand();
                tamper.CommandText = "DELETE FROM notes WHERE name = 'bir.md'; DELETE FROM notes_fts WHERE name = 'bir.md';";
                tamper.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // Taze bir Retrieve: derlem diskte değişmedi, yalnızca indeks satırı kurcalandı.
            var rebuilt = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index)).Build();
            Assert.Equal(1, rebuilt.ExitCode); // Manifest aynı ama satır kurcalanmış; Build bunu görmeli.
            Assert.Equal(["bir.md"], rebuilt.Missing); // Yalnızca kurcalanan not eksik görünmeli.
            Assert.Empty(rebuilt.Extra);

            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            var verified = retrieve.VerifyIndex();
            Assert.Equal(rebuilt.Missing, verified.Missing); // VerifyIndex, Build'in kendi doğrulamasıyla aynı cevabı vermeli.
            Assert.Equal(rebuilt.Extra, verified.Extra);
            Assert.Equal(rebuilt.ExitCode, verified.ExitCode);

            var doctorVerdict = new Doctor().VerifyIndex(retrieve);
            Assert.Equal(rebuilt.Missing, doctorVerdict.Missing); // Doctor'ın ikinci bir görüşü yok.
            Assert.Equal(rebuilt.Extra, doctorVerdict.Extra);
            Assert.Equal(rebuilt.ExitCode, doctorVerdict.ExitCode);

            var health = new Doctor().IndexHealth(retrieve);
            Assert.Equal("index-mismatch", health.Code);
            Assert.Equal(HealthLevel.Error, health.Level);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }

    private static string WriteNote(string vault, string name, string body, string title = "İndeks notu", string aliases = "", string tags = "")
    {
        var concepts = Path.Combine(vault, "knowledge", "concepts");
        Directory.CreateDirectory(concepts);
        var path = Path.Combine(concepts, name);
        File.WriteAllText(path, $"---\nyazan: codex\nmodel: gpt-5\ntitle: {title}\naliases: [{aliases}]\ntags: [{tags}]\nsources: [2026-09-10.md]\ncreated: 2026-09-10\nupdated: 2026-09-10\n---\n{body}");
        return path;
    }

    private static Manifest ReadManifest(string index)
    {
        using var connection = new SqliteConnection($"Data Source={index}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation, manifest_digest, built_at FROM oom_index_meta";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new Manifest(reader.GetInt64(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)));
    }

    private static IReadOnlyList<string> Candidates(string index, string term)
    {
        using var connection = new SqliteConnection($"Data Source={index}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM notes_fts WHERE notes_fts MATCH $term";
        command.Parameters.AddWithValue("$term", $"\"{term.Replace("\"", "\"\"")}\"");
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private sealed record Manifest(long Generation, string Digest, DateTimeOffset BuiltAt);
}
