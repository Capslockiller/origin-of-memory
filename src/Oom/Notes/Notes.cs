using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

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

    public string IndexableText(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        var builder = new StringBuilder();
        builder.AppendLine(note.Title);
        if (note.Aliases.Count > 0)
            builder.AppendLine(string.Join(' ', note.Aliases));
        if (note.Tags.Count > 0)
            builder.AppendLine(string.Join(' ', note.Tags));
        builder.Append(IndexableBody(note));
        return builder.ToString();
    }

    internal static string IndexableBody(Note note)
    {
        var body = HtmlComment.Replace(note.Body, string.Empty);
        return RetiredAnchor.Replace(body, string.Empty).Trim();
    }

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
