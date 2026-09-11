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
