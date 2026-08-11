using sutty.UI.Helpers;
using System.Text;

var screen = new VtScreenBuffer(columns: 20, rows: 5, maxScrollback: 3);

Feed("\x1b[?25labc\rZ");
Assert(Line(0) == "Zbc", "carriage return overwrites at the current row");

Feed("\x1b[2;3Hhello\x1b[2D!\x1b[K");
Assert(Line(1) == "  hel!", "cursor addressing and erase-line are interpreted");

Feed("\x1b[31mRED\x1b[0m");
Assert(!screen.Render().Contains('\x1b'), "SGR is consumed instead of rendered");
Assert(screen.Render().Contains("RED"), "styled text remains visible");

screen.Reset();
Feed("\x1b[?25lmain");
Feed("\x1b[?1049hALT");
Assert(screen.IsAlternateScreen && screen.Render().Contains("ALT"), "alternate screen is entered");
Assert(!screen.Render().Contains("main"), "alternate screen does not expose main screen");
Feed("\x1b[?1049l");
Assert(!screen.IsAlternateScreen && screen.Render().Contains("main"), "main screen is restored");

screen.Reset();
var korean = Encoding.UTF8.GetBytes("한글");
screen.Feed(korean.AsSpan(0, 2));
screen.Feed(korean.AsSpan(2));
Feed("\x1b[?25l");
Assert(screen.Render().Contains("한글"), "split UTF-8 sequences decode incrementally");

screen.Reset();
Feed("\x1b[?25l1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7");
Assert(screen.Render().Split('\n').Length <= 8, "scrollback remains bounded");
Assert(screen.Render().Contains('7'), "scrolling preserves newest output");

string? response = null;
screen.ResponseRequested += value => response = value;
Feed("\x1b[3;4H\x1b[6n");
Assert(response == "\x1b[3;4R", "cursor-position queries receive a VT response");

Feed("\x1b[?1h");
Assert(screen.ApplicationCursorKeys, "DECCKM enables SS3 application cursor keys");
Feed("\x1b[?1l");
Assert(!screen.ApplicationCursorKeys, "DECCKM reset restores normal cursor keys");

Console.WriteLine("Terminal VT self-test passed.");
return;

void Feed(string value) => screen.Feed(Encoding.UTF8.GetBytes(value));

string Line(int index) => screen.Render().Split('\n')[index];

static void Assert(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"Self-test failed: {description}");
}
