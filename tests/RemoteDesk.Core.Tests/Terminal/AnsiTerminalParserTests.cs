using FluentAssertions;
using RemoteDesk.Core.Terminal;
using Xunit;

namespace RemoteDesk.Core.Tests.Terminal;

public class AnsiTerminalParserTests
{
    [Fact]
    public void StripAnsi_RemovesEscapeSequences()
    {
        string raw = "\x1b[31mError:\x1b[0m File not found\x1b[2K\r\n";
        string stripped = AnsiTerminalParser.StripAnsi(raw);

        stripped.Should().Be("Error: File not found\r\n");
    }

    [Fact]
    public void Parse_ExtractsColorsAndFormatting()
    {
        string raw = "\x1b[32mSuccess:\x1b[0m \x1b[1;34mDone\x1b[0m";
        var spans = AnsiTerminalParser.Parse(raw);

        spans.Should().HaveCount(3);

        // 1. "Success:" in Green (#4CAF50)
        spans[0].Text.Should().Be("Success:");
        spans[0].ColorHex.Should().Be("#4CAF50");
        spans[0].IsBold.Should().BeFalse();

        // 2. " " in Default (#D4D4D4)
        spans[1].Text.Should().Be(" ");
        spans[1].ColorHex.Should().Be("#D4D4D4");

        // 3. "Done" in Blue (#2196F3) Bold
        spans[2].Text.Should().Be("Done");
        spans[2].ColorHex.Should().Be("#2196F3");
        spans[2].IsBold.Should().BeTrue();
    }

    [Fact]
    public void TerminalBuffer_HandlesLinesAndBackspace()
    {
        var buffer = new TerminalBuffer(maxLines: 100);

        buffer.Append("Hello\r\n");
        buffer.Append("Worlld\b\bd"); // "Worlld" -> backspace 'd' -> backspace 'l' -> append 'd' -> "World"
        buffer.Append("\r\n");

        var text = buffer.GetFullText();
        text.Should().Contain("Hello");
        text.Should().Contain("World");
        buffer.LineCount.Should().Be(2);
    }

    [Fact]
    public void TerminalBuffer_HandlesClearScreen()
    {
        var buffer = new TerminalBuffer(maxLines: 100);

        buffer.Append("Line 1\r\nLine 2\r\n");
        buffer.LineCount.Should().Be(2);

        // Clear screen sequence
        buffer.Append("\x1b[2JNew Screen\r\n");

        var text = buffer.GetFullText();
        text.Should().NotContain("Line 1");
        text.Should().Contain("New Screen");
        buffer.LineCount.Should().Be(1);
    }

    [Fact]
    public void TerminalBuffer_TrimsWhenExceedingMaxLines()
    {
        var buffer = new TerminalBuffer(maxLines: 5);

        for (int i = 1; i <= 10; i++)
        {
            buffer.Append($"Line {i}\r\n");
        }

        buffer.LineCount.Should().Be(5);
        var text = buffer.GetFullText();
        text.Should().NotContain("Line 1\r\n");
        text.Should().NotContain("Line 5\r\n");
        text.Should().Contain("Line 6");
        text.Should().Contain("Line 10");
    }
}
