using System.Text.RegularExpressions;

namespace RemoteDesk.Core.Terminal;

public record AnsiSpan(string Text, string ColorHex, bool IsBold);

public static class AnsiTerminalParser
{
    // Regex for matching ANSI escape sequences:
    // ESC [ ... command
    // ESC ] ... OSC command (like window title)
    // ESC ( ... charset
    private static readonly Regex AnsiRegex = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled);

    public static string StripAnsi(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return AnsiRegex.Replace(input, string.Empty);
    }

    public static IReadOnlyList<AnsiSpan> Parse(string input)
    {
        var spans = new List<AnsiSpan>();
        if (string.IsNullOrEmpty(input)) return spans;

        var matches = AnsiRegex.Matches(input);
        int lastIndex = 0;
        string currentColor = "#D4D4D4"; // default light gray / white
        bool isBold = false;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                string text = input.Substring(lastIndex, match.Index - lastIndex);
                spans.Add(new AnsiSpan(text, currentColor, isBold));
            }

            string seq = match.Value;
            if (seq.StartsWith("\x1b[") && seq.EndsWith('m'))
            {
                string codesStr = seq.Substring(2, seq.Length - 3);
                if (string.IsNullOrEmpty(codesStr) || codesStr == "0")
                {
                    currentColor = "#D4D4D4";
                    isBold = false;
                }
                else
                {
                    var parts = codesStr.Split(';');
                    foreach (var part in parts)
                    {
                        if (int.TryParse(part, out int code))
                        {
                            switch (code)
                            {
                                case 0:
                                    currentColor = "#D4D4D4";
                                    isBold = false;
                                    break;
                                case 1:
                                    isBold = true;
                                    break;
                                case 30: currentColor = "#1E1E1E"; break; // Black
                                case 31: currentColor = "#F44336"; break; // Red
                                case 32: currentColor = "#4CAF50"; break; // Green
                                case 33: currentColor = "#FFEB3B"; break; // Yellow
                                case 34: currentColor = "#2196F3"; break; // Blue
                                case 35: currentColor = "#9C27B0"; break; // Magenta
                                case 36: currentColor = "#00BCD4"; break; // Cyan
                                case 37: currentColor = "#E0E0E0"; break; // White
                                case 90: currentColor = "#757575"; break; // Bright Black (Gray)
                                case 91: currentColor = "#EF5350"; break; // Bright Red
                                case 92: currentColor = "#66BB6A"; break; // Bright Green
                                case 93: currentColor = "#FFEE58"; break; // Bright Yellow
                                case 94: currentColor = "#42A5F5"; break; // Bright Blue
                                case 95: currentColor = "#AB47BC"; break; // Bright Magenta
                                case 96: currentColor = "#26C6DA"; break; // Bright Cyan
                                case 97: currentColor = "#FFFFFF"; break; // Bright White
                            }
                        }
                    }
                }
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < input.Length)
        {
            string remaining = input.Substring(lastIndex);
            spans.Add(new AnsiSpan(remaining, currentColor, isBold));
        }

        return spans;
    }
}
