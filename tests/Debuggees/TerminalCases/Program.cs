// What the terminal's tests run in it, the same bytes on every system: on Windows the console is first set to read and
// write VT sequences and UTF-8, as a program drawing in a terminal does there. The first word is the case.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

Terminal.Prepare();
// The bytes as they are, on macOS and Linux past Console, which edits an input line itself and asks for the keypad's
// application keys as it first writes.
Stream output = OperatingSystem.IsWindows() ? Console.OpenStandardOutput() : Descriptor(1, FileAccess.Write);
Stream input = OperatingSystem.IsWindows() ? Console.OpenStandardInput() : Descriptor(0, FileAccess.Read);
switch (args)
{
    case ["draw", string columns]:
        // As vim or less draw: the alternate screen, cursor moves, partial erases, a scroll region, colours, a wide
        // character at the right edge; then the shell's screen back once a line is typed.
        Write("\e[?1049h\e[H\e[2J"
            + "1111111111\r\n2222222222\r\n3333333333\r\n4444444444\e[2;3H\e[K\e[3;5H\e[1K\e[4;1H\e[2K!"
            + "\e[5;1Htop\r\na\r\nb\r\nc\r\nbottom\e[6;8r\e[8;1H\nd\e[r"
            + "\e[10;1H\e[1;31mA\e[38;5;208mB\e[38;2;10;20;30mC\e[38:2::40:50:60mD\e[0;44mE\e[0mF"
            + $"\e[11;{int.Parse(columns) - 3}Habcd漢");
        Console.ReadLine();
        Write("\e[?1049l");
        break;
    case ["ask"]:
        // Where the cursor is, as a program asks before drawing, and what the terminal is, each answer read back.
        using (Terminal.Raw())
        {
            Write("\e[5;10H\e[6n");
            string at = ReadTo('R');
            Write("\e[c");
            string kind = ReadTo('c');
            Terminal.Restored();
            Write($"\r\nat {at[2..^1]} is {kind[2..^1]}\r\n");
        }
        break;
    case ["keys", .. string[] modes]:
        // The keys as read, as cat -v shows them, a line at Enter, until Ctrl+C; "application" first asks for the
        // application cursor keys.
        if (modes is ["application"])
        {
            Write("\e[?1h");
        }
        using (Terminal.Raw())
        {
            Write("keys?\r\n");
            StringBuilder line = new();
            for (int b = input.ReadByte(); b is >= 0 and not 3; b = input.ReadByte())
            {
                if (b == '\r')
                {
                    Write($"{line}\r\n");
                    line.Clear();
                }
                else
                {
                    line.Append(b < 32 ? $"^{(char)(b + 64)}" : ((char)b).ToString());
                }
            }
        }
        if (modes is ["application"])
        {
            Write("\e[?1l");
        }
        break;
    case ["mouse", .. string[] modes]:
        // The mouse reports as read, in SGR's form (1006) after the modes asked for (1002, 1003), each on a line of its
        // own as cat -v shows it, until Ctrl+C; the modes put back after.
        Write(string.Concat(modes.Select(mode => $"\e[?{mode}h")) + "\e[?1006h");
        using (Terminal.Raw())
        {
            Write("mouse?\r\n");
            StringBuilder report = new();
            for (int b = input.ReadByte(); b is >= 0 and not 3; b = input.ReadByte())
            {
                report.Append(b < 32 ? $"^{(char)(b + 64)}" : ((char)b).ToString());
                if (b is 'M' or 'm')
                {
                    Write($"{report}\r\n");
                    report.Clear();
                }
            }
        }
        Write("\e[?1006l" + string.Concat(modes.Select(mode => $"\e[?{mode}l")));
        break;
    case ["lines", string from, string to]:
        Lines(long.Parse(from), long.Parse(to));
        break;
    case ["long", string width]:
        // A line one wider than the screen, then 200 numbered lines past it.
        Write(new string('0', int.Parse(width)) + "7\n");
        Lines(1, 200);
        break;
    case ["size"]:
        Write($"{Console.WindowHeight} {Console.WindowWidth}\n");
        break;
    case ["hundred"]:
        Write(string.Concat(Enumerable.Range(10, 50)) + "\n");
        break;
    case ["accent"]:
        // Résumé.pdf as macOS's file system spells it, each é an e and a combining acute.
        Write("Résumé.pdf\n");
        break;
    case ["emoji"]:
        // A rocket, then a check mark and a lightning bolt, each two columns wide.
        Write("\U0001F680x\n✅⚡y\n");
        break;
    case ["wait", string file, string from, string to]:
        // Lines that come once the test has made the file.
        while (!File.Exists(file))
        {
            Thread.Sleep(50);
        }
        Lines(long.Parse(from), long.Parse(to));
        break;
    case ["runs", string columns]:
        // Plain text on the alternate screen, a row each case: at the last column, unwrapped, inserted, line-drawn, coloured,
        // repeated, and split between writes; the shell's screen back once a line is typed.
        int across = int.Parse(columns);
        Write("\e[?1049h\e[H\e[2J");
        Write($"\e[1;{across - 3}Hwxyz!");
        Write($"\e[?7l\e[3;{across - 2}H12345\e[?7h");
        Write("\e[4;1Habcdef\e[4;3H\e[4hXY\e[4l");
        Write("\e[5;1H漢字ab\e[5;2H\e[4hQ\e[4l");
        Write("\e[6;1H\e(0lqqk\e(B ok");
        Write("\e[7;1Hsplit-");
        Thread.Sleep(100);
        Write("run");
        output.Write([.. Encoding.UTF8.GetBytes("\e[8;1Hcaf"), 0xC3]);
        output.Flush();
        Thread.Sleep(100);
        output.Write([0xA9, (byte)'!']);
        output.Flush();
        Write("\e[9;1H\e[1;");
        Thread.Sleep(100);
        Write("31mred\e[0m plain");
        Write($"\e[10;1H{new string('-', (across * 2) + 5)}");
        Write($"\e[13;{across - 1}Hab");
        Thread.Sleep(100);
        Write("cd");
        Write($"\e[15;1H{string.Concat(Enumerable.Range(0, across).Select(i => (char)('0' + (i % 10))))}\e[15;1H\e[4h<<\e[4l");
        Write($"\e[16;{across - 1}H\e[4hXYZ\e[4l");
        Write("\e[18;1Hab\e[3b");
        Console.ReadLine();
        Write("\e[?1049l");
        break;
    case ["colours", string mode, string rows]:
        // A build's log in colour on the alternate screen, its lines numbered, a step each time the test makes the file
        // next-N: "scroll" fills the screen and brings one line more up its foot, "redraw" fills it and writes it all
        // again, its first line numbered on, as a full-screen program draws, and "row" writes one line at the top of an
        // empty screen and writes it again there, numbered on; until the test makes done, which gives the shell's screen back.
        int height = int.Parse(rows);
        string Screen(int first) => string.Join("\r\n", Enumerable.Range(2, height - 1).Select(Coloured).Prepend(Coloured(first)));
        Write("\e[?1049h\e[H\e[2J");
        int number = mode == "scroll" ? height : 1;
        Write(mode == "row" ? Coloured(number) : mode == "scroll" ? string.Join("\r\n", Enumerable.Range(1, height).Select(Coloured)) : Screen(number));
        for (int step = 1; ; step++)
        {
            string next = $"next-{step}";
            while (!File.Exists(next) && !File.Exists("done"))
            {
                Thread.Sleep(2);
            }
            if (!File.Exists(next))
            {
                break;
            }
            number++;
            Write(mode == "row" ? $"\e[1;1H{Coloured(number)}\e[K" : mode == "scroll" ? $"\r\n{Coloured(number)}" : $"\e[H{Screen(number)}");
        }
        Write("\e[?1049l");
        break;
    case ["log", string file, string count]:
        // A build's log of plain lines, once the test has made the file, then a last line saying it is done.
        while (!File.Exists(file))
        {
            Thread.Sleep(50);
        }
        StringBuilder log = new();
        for (int i = 1, n = int.Parse(count); i <= n; i++)
        {
            log.Append($"line {i:D7} of the build: Compiling Passenger.Terminal/TerminalScreen.cs, 0 warnings\n");
            if (log.Length > 16 * 1024 || i == n)
            {
                Write(log.ToString());
                log.Clear();
            }
        }
        Write("done\n");
        break;
    case ["variables", .. string[] names]:
        // Each of these variables the program has, as env lists it, then a line saying they are listed.
        Write(string.Concat(names.Where(name => Environment.GetEnvironmentVariable(name) is not null)
            .Select(name => $"{name}={Environment.GetEnvironmentVariable(name)}\n")) + "listed\n");
        break;
    default:
        Console.Error.WriteLine($"No case '{string.Join(' ', args)}'");
        return 2;
}
return 0;

static Stream Descriptor(int descriptor, FileAccess access) => new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(descriptor, ownsHandle: false), access, 1);

void Write(string text)
{
    output.Write(Encoding.UTF8.GetBytes(text));
    output.Flush();
}

void Lines(long from, long to)
{
    StringBuilder lines = new();
    for (long i = from; i <= to; i++)
    {
        lines.Append(i).Append('\n');
        if (lines.Length > 16 * 1024)
        {
            Write(lines.ToString());
            lines.Clear();
        }
    }
    Write(lines.ToString());
}

// A line of a build's log as a compiler colours it, thirteen runs in eight styles, its number first.
static string Coloured(int number) =>
    $"\e[2m{number:D4}\e[0m \e[1;32mCompiling\e[0m terminal \e[2m(\e[0;36msrc/Terminal\e[0;2m)\e[0m \e[1;33mwarning\e[0m: "
    + $"\e[4munused\e[24m \e[38;5;208mvariable\e[0m \e[38;2;120;160;255m`x{number}`\e[0m";

string ReadTo(char last)
{
    StringBuilder read = new();
    for (int b = input.ReadByte(); b >= 0; b = input.ReadByte())
    {
        read.Append((char)b);
        if (b == last)
        {
            break;
        }
    }
    return read.ToString();
}

/// <summary>The console as a program drawing in a terminal sets it: UTF-8 and VT sequences both ways on Windows, and a raw
/// mode (no echo, no line editing, no signals from keys) while it reads keys itself, put back after.</summary>
internal static class Terminal
{
    private const int StdInput = -10;
    private const int StdOutput = -11;

    public static void Prepare()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            nint output = GetStdHandle(StdOutput);
            GetConsoleMode(output, out uint mode);
            // Processed output, wrap at the line's end, and VT processing.
            SetConsoleMode(output, mode | 0x1 | 0x2 | 0x4);
        }
    }

    private static string? kept;
    private static uint keptMode;

    public static IDisposable Raw()
    {
        if (OperatingSystem.IsWindows())
        {
            nint input = GetStdHandle(StdInput);
            GetConsoleMode(input, out keptMode);
            // VT input alone: no line input, no echo, no Ctrl+C as a signal.
            SetConsoleMode(input, 0x200);
        }
        else
        {
            kept = Stty("-g").Trim();
            Stty("raw", "-echo");
        }
        return new Restore();
    }

    public static void Restored()
    {
        if (OperatingSystem.IsWindows())
        {
            SetConsoleMode(GetStdHandle(StdInput), keptMode);
        }
        else if (kept is { } settings)
        {
            Stty(settings);
            kept = null;
        }
    }

    private sealed class Restore : IDisposable
    {
        public void Dispose() => Restored();
    }

    private static string Stty(params string[] words)
    {
        ProcessStartInfo start = new("stty", words) { UseShellExecute = false, RedirectStandardOutput = true };
        using Process stty = Process.Start(start)!;
        string said = stty.StandardOutput.ReadToEnd();
        stty.WaitForExit();
        return said;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(nint handle, uint mode);
}
