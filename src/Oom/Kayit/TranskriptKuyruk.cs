using System.Text;

namespace Oom.Contracts;

// Only a verified line offset can distinguish an original record from a replay.
internal static class TranskriptKuyruk
{
    internal sealed record Input(string Text, long Bytes, bool Tail = false, int StartIndex = 0, string? RemovedCursor = null, string? Project = null,
        long StartByte = 0, IReadOnlyList<long>? LineOffsets = null);

    internal static bool IsBusy(IOException error, string path, DateTimeOffset now) =>
        (error.HResult & 0xffff) is 32 or 33 && File.GetLastWriteTimeUtc(path) > now.UtcDateTime.AddHours(-24);

    internal static Input Read(string path, Imlec.Cursor? cursor)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long attempted = 0;
        if (cursor is { End: >= 0, ByteOffset: { } offset } && offset >= 0 && offset < stream.Length && AtLineStart(stream, offset, ref attempted))
        {
            stream.Position = offset;
            using var line = new MemoryStream();
            while (stream.ReadByte() is var value && value >= 0)
            {
                line.WriteByte((byte)value);
                if (value == '\n') break;
            }
            attempted += stream.Position - offset;
            var needle = Encoding.UTF8.GetBytes("\"uuid\":\"" + cursor.Last + "\"");
            if (line.GetBuffer().AsSpan(0, (int)line.Length).IndexOf(needle) >= 0)
            {
                var start = stream.Position;
                var tail = ReadRemaining(stream);
                return new(tail.Text, attempted + stream.Position - start, true, cursor.End + 1, cursor.Last, cursor.Project, start, tail.Offsets);
            }
        }
        // Legacy anchors, malformed offsets and rewritten files use the first UUID occurrence.
        stream.Position = 0;
        var full = ReadRemaining(stream);
        return new(full.Text, attempted + stream.Position, LineOffsets: full.Offsets);
    }

    private static bool AtLineStart(FileStream stream, long offset, ref long bytesRead)
    {
        if (offset == 0) return true;
        stream.Position = offset - 1;
        bytesRead++;
        if (stream.ReadByte() == '\n') return true;
        if (offset != 3) return false;
        stream.Position = 0;
        Span<byte> bom = stackalloc byte[3];
        stream.ReadExactly(bom);
        bytesRead += bom.Length;
        return bom.SequenceEqual(Encoding.UTF8.Preamble);
    }

    private static (string Text, long[] Offsets) ReadRemaining(FileStream stream)
    {
        var start = stream.Position;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var bom = start == 0 && bytes.StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        var offsets = new List<long> { start + bom };
        // Count raw bytes before decoding: incomplete UTF-8 may produce replacement characters.
        for (var i = bom; i < bytes.Length; i++)
            if (bytes[i] == '\n') offsets.Add(start + i + 1);
        return (Encoding.UTF8.GetString(bytes[bom..]), offsets.ToArray());
    }

    internal static IEnumerable<(string Text, long Offset)> Lines(string text, long offset = 0, IReadOnlyList<long>? lineOffsets = null)
    {
        var index = 0;
        foreach (var line in text.Split('\n'))
        {
            yield return (line, lineOffsets is null ? offset : lineOffsets[index]);
            if (lineOffsets is null) offset += Encoding.UTF8.GetByteCount(line) + 1;
            index++;
        }
    }
}
