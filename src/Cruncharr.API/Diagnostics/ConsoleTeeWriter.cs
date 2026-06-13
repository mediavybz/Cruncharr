using System.Text;

namespace Cruncharr.API.Diagnostics;

/// <summary>
/// A TextWriter that forwards everything to the original console writer AND tees
/// notable lines into the in-memory log store. Many download utilities (the HLS
/// segment downloader, the MPD parser, etc.) log via Console.Write* rather than
/// ILogger, so without this their failures never reach the diagnostics API.
///
/// To avoid flooding the ring buffer with progress spam, stdout is captured only
/// for lines that look like problems; stderr is captured in full (it is where the
/// downloaders write their errors).
/// </summary>
public sealed class ConsoleTeeWriter : TextWriter
{
    private static readonly string[] ProblemHints =
    {
        "error", "fail", "exception", "denied", "forbidden", "invalid",
        "403", "404", "401", "timeout", "timed out", "could not", "unable", "broken"
    };

    private readonly TextWriter _original;
    private readonly InMemoryLogStore _store;
    private readonly string _category;
    private readonly string _level;
    private readonly bool _captureAll;
    private readonly StringBuilder _lineBuffer = new();
    private readonly object _lock = new();

    public ConsoleTeeWriter(TextWriter original, InMemoryLogStore store, string category, string level, bool captureAll)
    {
        _original = original;
        _store = store;
        _category = category;
        _level = level;
        _captureAll = captureAll;
    }

    public override Encoding Encoding => _original.Encoding;

    public override void Write(char value)
    {
        _original.Write(value);
        lock (_lock)
        {
            if (value == '\n')
            {
                FlushLine();
            }
            else if (value != '\r')
            {
                _lineBuffer.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        _original.Write(value);
        if (string.IsNullOrEmpty(value)) return;
        lock (_lock)
        {
            foreach (var ch in value)
            {
                if (ch == '\n') FlushLine();
                else if (ch != '\r') _lineBuffer.Append(ch);
            }
        }
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    private void FlushLine()
    {
        var line = _lineBuffer.ToString();
        _lineBuffer.Clear();
        if (line.Length == 0) return;

        if (!_captureAll && !LooksLikeProblem(line)) return;

        _store.Add(new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = _level,
            Category = _category,
            Message = line
        });
    }

    private static bool LooksLikeProblem(string line)
    {
        foreach (var hint in ProblemHints)
        {
            if (line.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
