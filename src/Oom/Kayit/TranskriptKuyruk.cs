using System.Text;

namespace Oom.Contracts;

// Byte offsets keep UTF-8 characters intact even when a chunk splits one.
internal static class TranskriptKuyruk
{
    internal sealed record Input(string Text, long Bytes, bool Tail = false, int StartIndex = 0, string? RemovedCursor = null, string? Project = null);

    internal static Input Read(string path, Imlec.Cursor? cursor)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (cursor is null)
        {
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return new(text, stream.Position);
        }
        var needle = Encoding.ASCII.GetBytes("\"uuid\":\"" + cursor.Last + "\"");
        var chunks = new List<byte[]>();
        var overlap = Array.Empty<byte>();
        var position = stream.Length;
        long bytes = 0;
        while (position > 0)
        {
            var size = (int)Math.Min(64 * 1024, position);
            position -= size;
            stream.Position = position;
            var chunk = new byte[size];
            stream.ReadExactly(chunk);
            bytes += size;
            chunks.Add(chunk);
            var search = new byte[size + overlap.Length];
            chunk.CopyTo(search, 0);
            overlap.CopyTo(search, size);
            var match = search.AsSpan().IndexOf(needle);
            if (match >= 0)
            {
                var buffer = Join(chunks);
                var newline = Array.IndexOf(buffer, (byte)'\n', match + needle.Length);
                var start = newline < 0 ? buffer.Length : newline + 1;
                return new(Encoding.UTF8.GetString(buffer, start, buffer.Length - start), bytes, true, cursor.End + 1, cursor.Last, cursor.Project);
            }
            overlap = search[..Math.Min(search.Length, needle.Length - 1)];
        }
        // A removed or rewritten cursor must preserve the full-parse fallback.
        return new(Encoding.UTF8.GetString(Join(chunks)).TrimStart('\uFEFF'), bytes);
    }

    private static byte[] Join(List<byte[]> chunks)
    {
        var buffer = new byte[chunks.Sum(c => c.Length)];
        var offset = 0;
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            chunks[i].CopyTo(buffer, offset);
            offset += chunks[i].Length;
        }
        return buffer;
    }
}
