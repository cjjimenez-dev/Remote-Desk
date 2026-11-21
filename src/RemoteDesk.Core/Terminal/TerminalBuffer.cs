using System.Text;

namespace RemoteDesk.Core.Terminal;

public sealed class TerminalBuffer
{
    private readonly object _lock = new();
    private readonly StringBuilder _currentLine = new();
    private readonly List<string> _lines = new();
    private readonly int _maxLines;

    public int LineCount
    {
        get
        {
            lock (_lock) return _lines.Count + (_currentLine.Length > 0 ? 1 : 0);
        }
    }

    public TerminalBuffer(int maxLines = 2000)
    {
        _maxLines = maxLines;
    }

    public void Append(string rawInput)
    {
        if (string.IsNullOrEmpty(rawInput)) return;

        lock (_lock)
        {
            if (rawInput.Contains("\x1b[2J") || rawInput.Contains("\x1b[H\x1b[2J"))
            {
                _lines.Clear();
                _currentLine.Clear();
            }

            string stripped = AnsiTerminalParser.StripAnsi(rawInput);

            for (int i = 0; i < stripped.Length; i++)
            {
                char c = stripped[i];

                if (c == '\r')
                {
                    if (i + 1 < stripped.Length && stripped[i + 1] == '\n')
                    {
                        CommitCurrentLine();
                        i++;
                    }
                    else
                    {
                        _currentLine.Clear();
                    }
                }
                else if (c == '\n')
                {
                    CommitCurrentLine();
                }
                else if (c == '\b')
                {
                    if (_currentLine.Length > 0)
                    {
                        _currentLine.Length--;
                    }
                }
                else if (c == '\t')
                {
                    _currentLine.Append("    ");
                }
                else if (!char.IsControl(c) || c == ' ')
                {
                    _currentLine.Append(c);
                }
            }
        }
    }

    private void CommitCurrentLine()
    {
        _lines.Add(_currentLine.ToString());
        _currentLine.Clear();

        if (_lines.Count > _maxLines)
        {
            _lines.RemoveRange(0, _lines.Count - _maxLines);
        }
    }

    public string GetFullText()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            foreach (var line in _lines)
            {
                sb.AppendLine(line);
            }
            if (_currentLine.Length > 0)
            {
                sb.Append(_currentLine.ToString());
            }
            return sb.ToString();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
            _currentLine.Clear();
        }
    }
}
