using System.Globalization;
using System.IO;

namespace BusPulse.Services.Trips.Gtfs;

/// <summary>
/// Reads lines from a stream without ever holding more than one buffer. Lines longer than the cap are skipped (counted in
/// <see cref="SkippedLines"/>) instead of growing memory, because the feed is untrusted input.
/// </summary>
internal sealed class GtfsLineReader : IDisposable
{
    private readonly TextReader _reader;
    private readonly char[] _buffer;
    private readonly int _maxLineLength;
    private int _start;
    private int _end;
    private bool _eof;
    private bool _discarding;

    public GtfsLineReader(Stream stream, int maxLineLength = 16 * 1024)
    {
        _reader = new StreamReader(stream, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
        _maxLineLength = maxLineLength;
        _buffer = new char[Math.Max(64 * 1024, maxLineLength * 2)];
    }

    public int SkippedLines { get; private set; }

    /// <summary>The next line without its terminator, or false at the end. The span is valid until the next call.</summary>
    public bool TryRead(out ReadOnlySpan<char> line)
    {
        while (true)
        {
            var window = _buffer.AsSpan(_start, _end - _start);
            var newline = window.IndexOf('\n');
            if (newline >= 0)
            {
                var lineStart = _start;
                var length = newline;
                _start += newline + 1;
                if (_discarding)
                {
                    _discarding = false;
                    SkippedLines++;
                    continue;
                }

                if (length > 0 && _buffer[lineStart + length - 1] == '\r')
                {
                    length--;
                }

                if (length > _maxLineLength)
                {
                    SkippedLines++;
                    continue;
                }

                line = _buffer.AsSpan(lineStart, length);
                return true;
            }

            if (_eof)
            {
                var rest = window.Length;
                var restStart = _start;
                _start = _end;
                if (_discarding)
                {
                    _discarding = false;
                    SkippedLines++;
                    break;
                }

                if (rest > 0 && _buffer[restStart + rest - 1] == '\r')
                {
                    rest--;
                }

                if (rest > 0 && rest <= _maxLineLength)
                {
                    line = _buffer.AsSpan(restStart, rest);
                    return true;
                }

                if (rest > _maxLineLength)
                {
                    SkippedLines++;
                }

                break;
            }

            if (window.Length > _maxLineLength)
            {
                // A line already over the cap with no end in sight: drop what we have and skip to the next newline.
                _discarding = true;
                _start = _end = 0;
            }
            else if (_start > 0)
            {
                window.CopyTo(_buffer);
                _end = window.Length;
                _start = 0;
            }

            var read = _reader.Read(_buffer, _end, _buffer.Length - _end);
            if (read <= 0)
            {
                _eof = true;
            }
            else
            {
                _end += read;
            }
        }

        line = default;
        return false;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>Splits one CSV line into fields, honouring double quotes. Reuse one instance per file.</summary>
internal sealed class GtfsCsvSplitter
{
    public const int MaxColumns = 64;
    private readonly int[] _starts = new int[MaxColumns];
    private readonly int[] _lengths = new int[MaxColumns];

    public int Count { get; private set; }

    /// <summary>Splits <paramref name="line"/>; quoted fields lose their outer quotes (doubled quotes stay doubled in raw spans).</summary>
    public void Split(ReadOnlySpan<char> line)
    {
        Count = 0;
        var i = 0;
        var n = line.Length;
        while (Count < MaxColumns)
        {
            if (i < n && line[i] == '"')
            {
                var j = i + 1;
                while (j < n)
                {
                    if (line[j] == '"')
                    {
                        if (j + 1 < n && line[j + 1] == '"')
                        {
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    j++;
                }

                _starts[Count] = i + 1;
                _lengths[Count] = Math.Min(j, n) - (i + 1);
                Count++;
                var comma = j < n ? line[j..].IndexOf(',') : -1;
                if (comma < 0)
                {
                    return;
                }

                i = j + comma + 1;
            }
            else
            {
                var comma = i < n ? line[i..].IndexOf(',') : -1;
                _starts[Count] = i;
                if (comma < 0)
                {
                    _lengths[Count] = n - i;
                    Count++;
                    return;
                }

                _lengths[Count] = comma;
                Count++;
                i += comma + 1;
            }
        }
    }

    public ReadOnlySpan<char> Raw(ReadOnlySpan<char> line, int column) =>
        column >= 0 && column < Count ? line.Slice(_starts[column], _lengths[column]) : default;

    /// <summary>Field text with doubled quotes collapsed, trimmed and capped in length.</summary>
    public string Text(ReadOnlySpan<char> line, int column, int maxLength = 160)
    {
        var raw = Raw(line, column).Trim();
        if (raw.IsEmpty)
        {
            return string.Empty;
        }

        if (raw.Length > maxLength)
        {
            raw = raw[..maxLength];
        }

        return raw.Contains("\"\"", StringComparison.Ordinal) ? raw.ToString().Replace("\"\"", "\"", StringComparison.Ordinal) : raw.ToString();
    }

    public static bool TryInt(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

/// <summary>Column lookup by header name, so column order never matters.</summary>
internal sealed class GtfsHeader
{
    private readonly Dictionary<string, int> _columns = new(StringComparer.OrdinalIgnoreCase);

    public static GtfsHeader Read(GtfsLineReader reader, GtfsCsvSplitter splitter, string fileName)
    {
        if (!reader.TryRead(out var line))
        {
            throw new InvalidDataException($"{fileName} is empty.");
        }

        if (line.Length > 0 && line[0] == '\uFEFF')
        {
            line = line[1..];
        }

        splitter.Split(line);
        var header = new GtfsHeader();
        for (var i = 0; i < splitter.Count; i++)
        {
            header._columns.TryAdd(splitter.Text(line, i, 64), i);
        }

        return header;
    }

    public int Find(string name) => _columns.TryGetValue(name, out var index) ? index : -1;

    public int Require(string name, string fileName) =>
        Find(name) is var index and >= 0 ? index : throw new InvalidDataException($"{fileName} has no {name} column.");
}

/// <summary>Stream wrapper that fails once more than the allowed number of bytes was read (zip bombs).</summary>
internal sealed class GtfsLimitedStream(Stream inner, long maxBytes, string name) : Stream
{
    private long _total;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    private int Count(int read)
    {
        _total += read;
        return _total > maxBytes ? throw new InvalidDataException($"{name} exceeds the {maxBytes} byte limit.") : read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
