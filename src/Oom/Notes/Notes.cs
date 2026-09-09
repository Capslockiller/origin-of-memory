using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

/// <summary>
/// The single frontmatter parser and the concept note contract (spec 6.4 and 7).
/// There is exactly one parser in the product on purpose: a tolerant second copy
/// is what turned <c>tags: [a, b</c> into an empty list in v0 (scar Y-096), so
/// this one is strict and refuses instead of guessing.
/// </summary>
public sealed class Notes
{
    private static readonly string[] RequiredKeys = ["title", "aliases", "tags", "sources", "created", "updated"];
    private static readonly Regex KeyLine = new(@"^(?<key>[A-Za-z_][A-Za-z0-9_-]*):(?<value>.*)$", RegexOptions.Compiled);
    private static readonly Regex Slug = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex RetiredAnchor = new(@"(?i)\b(?:emekli|emeklilik|retired)\s+(?:çapa\s+)?session:[A-Za-z0-9_.\-]+", RegexOptions.Compiled);
    private static readonly Regex WikiLink = new(@"\[\[(?<target>[^\]\|]+)(?:\|[^\]]+)?\]\](?<reason>[^\r\n]*)", RegexOptions.Compiled);
    private const string RelatedHeading = "## İlgili Kavramlar";
    private const string FrontmatterFence = "---";

    private readonly TurkishFold _fold = new();

    /// <summary>
    /// Strict frontmatter parse. Every one of the six required keys must be
    /// present with the documented type, the body must not be empty, and a
    /// malformed value is a <see cref="FormatException"/> — never a silent
    /// default. An invalid note is simply not indexed and <c>doctor</c> counts it.
    /// </summary>
    public Note Parse(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        var name = Path.GetFileName(path);
        var content = text.TrimStart('﻿');
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != FrontmatterFence)
            throw new FormatException($"{name}: frontmatter '---' ile başlamıyor.");

        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() != FrontmatterFence)
                continue;
            end = i;
            break;
        }

        if (end < 0)
            throw new FormatException($"{name}: frontmatter kapanmıyor.");

        var fields = ReadFields(name, lines[1..end]);
        foreach (var key in RequiredKeys)
        {
            if (!fields.ContainsKey(key))
                throw new FormatException($"{name}: '{key}' alanı eksik.");
        }

        var body = string.Join('\n', lines[(end + 1)..]).Trim();
        if (body.Length == 0)
            throw new FormatException($"{name}: gövde boş.");

        return new Note(
            name,
            ReadScalar(name, fields, "title"),
            ReadList(name, fields, "aliases"),
            ReadList(name, fields, "tags"),
            ReadList(name, fields, "sources"),
            ReadDate(name, fields, "created"),
            ReadDate(name, fields, "updated"),
            body);
    }

    /// <summary>
    /// Enforces the concept note contract of spec 7: ASCII kebab slug, no
    /// subdirectory, and an <c>## İlgili Kavramlar</c> section carrying at least
    /// two wikilinks, each with a reason sentence. Compile calls this before a
    /// single file is written; a violation drops the whole run.
    /// </summary>
    public Note Validate(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        if (note.Name.Contains('/') || note.Name.Contains('\\'))
            throw new FormatException($"{note.Name}: kavram notu alt dizine yazılamaz.");
        if (!note.Name.EndsWith(".md", StringComparison.Ordinal))
            throw new FormatException($"{note.Name}: uzantı '.md' olmalı.");

        var slug = note.Name[..^3];
        if (!Slug.IsMatch(slug))
            throw new FormatException($"{note.Name}: dosya adı ASCII kebab-case değil.");
        if (string.IsNullOrWhiteSpace(note.Title))
            throw new FormatException($"{note.Name}: 'title' boş.");
        if (note.Updated < note.Created)
            throw new FormatException($"{note.Name}: 'updated' 'created' tarihinden önce.");

        var related = ReadRelatedSection(note.Body)
            ?? throw new FormatException($"{note.Name}: '{RelatedHeading}' bölümü yok.");

        var withReason = 0;
        foreach (Match match in WikiLink.Matches(related))
        {
            var reason = match.Groups["reason"].Value.Trim(' ', '\t', '-', '—', ':', '.');
            if (reason.Length >= 3)
                withReason++;
        }

        if (withReason < 2)
            throw new FormatException($"{note.Name}: '{RelatedHeading}' en az iki gerekçeli [[wikilink]] ister.");

        return note;
    }

    /// <summary>
    /// The text that reaches the full text index: title, aliases, tags and body.
    /// Anchor comments and retired anchors are removed first — a retired anchor
    /// that stays searchable is how a dead session came back on every index
    /// rebuild in v0 (scars Y-023 and Y-024).
    /// </summary>
    public string IndexableText(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        var body = HtmlComment.Replace(note.Body, string.Empty);
        body = RetiredAnchor.Replace(body, string.Empty);

        var builder = new StringBuilder();
        builder.AppendLine(note.Title);
        if (note.Aliases.Count > 0)
            builder.AppendLine(string.Join(' ', note.Aliases));
        if (note.Tags.Count > 0)
            builder.AppendLine(string.Join(' ', note.Tags));
        builder.Append(body.Trim());
        return builder.ToString();
    }

    /// <summary>Index terms for a note; the query side uses the same folding.</summary>
    public IReadOnlyList<string> IndexTokens(Note note) => _fold.Tokenize(IndexableText(note));

    private string? ReadRelatedSection(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var heading = _fold.Fold(RelatedHeading);
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!_fold.Fold(lines[i].Trim()).StartsWith(heading, StringComparison.Ordinal))
                continue;
            start = i + 1;
            break;
        }

        if (start < 0)
            return null;

        var section = new List<string>();
        for (var i = start; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
                break;
            section.Add(lines[i]);
        }

        return string.Join('\n', section);
    }

    private static Dictionary<string, string> ReadFields(string name, IReadOnlyList<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var block = new List<string>();

        void Commit()
        {
            if (current is null)
                return;
            if (block.Count > 0)
                fields[current] = "[" + string.Join(", ", block) + "]";
            block.Clear();
            current = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("  - ", StringComparison.Ordinal))
            {
                if (current is null)
                    throw new FormatException($"{name}: liste öğesi bir alana bağlı değil: '{line.Trim()}'.");
                block.Add(line.TrimStart(' ', '-').Trim());
                continue;
            }

            var match = KeyLine.Match(line);
            if (!match.Success)
                throw new FormatException($"{name}: frontmatter satırı 'anahtar: değer' değil: '{line.Trim()}'.");

            Commit();
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim();
            if (fields.ContainsKey(key))
                throw new FormatException($"{name}: '{key}' alanı iki kez yazılmış.");

            if (value.Length == 0)
            {
                current = key;
                fields[key] = "[]";
                continue;
            }

            fields[key] = value;
        }

        Commit();
        return fields;
    }

    private static string ReadScalar(string name, IReadOnlyDictionary<string, string> fields, string key)
    {
        var value = fields[key].Trim();
        if (value.StartsWith('[') || value.Length == 0)
            throw new FormatException($"{name}: '{key}' bir metin olmalı.");
        return value.Trim('"', '\'');
    }

    private static IReadOnlyList<string> ReadList(string name, IReadOnlyDictionary<string, string> fields, string key)
    {
        var value = fields[key].Trim();
        if (!value.StartsWith('[') || !value.EndsWith(']'))
            throw new FormatException($"{name}: '{key}' bir liste olmalı ('[...]').");

        var inner = value[1..^1].Trim();
        if (inner.Length == 0)
            return [];

        return inner
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim('"', '\''))
            .ToArray();
    }

    private static DateOnly ReadDate(string name, IReadOnlyDictionary<string, string> fields, string key)
    {
        var value = fields[key].Trim().Trim('"', '\'');
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new FormatException($"{name}: '{key}' 'YYYY-MM-DD' biçiminde olmalı.");
        return date;
    }
}
